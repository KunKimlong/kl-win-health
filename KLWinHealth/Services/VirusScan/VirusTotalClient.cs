using System.Net;
using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace KLWinHealth.Services.VirusScan;

public record VirusTotalReport(bool Found, int Malicious, int Suspicious, int Total, string Link, string Message);

/// <summary>
/// VirusTotal v3 hash lookup (GET /api/v3/files/{sha256}, header x-apikey). Requests are queued so the
/// free-tier limit of 4 requests/minute is never exceeded. 404 = file unknown to VirusTotal.
/// </summary>
public class VirusTotalClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _queue = new(1, 1);
    private readonly Queue<DateTime> _recent = new();
    private const int PerMinute = 4;

    public async Task<VirusTotalReport> LookupAsync(string sha256, string apiKey, CancellationToken ct, Action<string>? onWaiting = null)
    {
        var link = $"https://www.virustotal.com/gui/file/{sha256}";
        await _queue.WaitAsync(ct);
        try
        {
            // Sliding one-minute window.
            while (true)
            {
                while (_recent.Count > 0 && DateTime.UtcNow - _recent.Peek() > TimeSpan.FromSeconds(61)) _recent.Dequeue();
                if (_recent.Count < PerMinute) break;
                var wait = TimeSpan.FromSeconds(61) - (DateTime.UtcNow - _recent.Peek());
                onWaiting?.Invoke($"VirusTotal free limit: waiting {Math.Ceiling(wait.TotalSeconds)}s…");
                await Task.Delay(wait, ct);
            }
            _recent.Enqueue(DateTime.UtcNow);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.virustotal.com/api/v3/files/{sha256}");
            req.Headers.Add("x-apikey", apiKey);
            req.Headers.UserAgent.ParseAdd("KLWinHealth/1.0");
            using var resp = await Http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new VirusTotalReport(false, 0, 0, 0, link, "Unknown file (not in VirusTotal)");
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return new VirusTotalReport(false, -1, 0, 0, link, "VirusTotal rejected the API key");
            if ((int)resp.StatusCode == 429)
                return new VirusTotalReport(false, -1, 0, 0, link, "VirusTotal quota exceeded — try later");
            if (!resp.IsSuccessStatusCode)
                return new VirusTotalReport(false, -1, 0, 0, link, $"VirusTotal error {(int)resp.StatusCode}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var stats = doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("last_analysis_stats");
            int Get(string n) => stats.TryGetProperty(n, out var v) ? v.GetInt32() : 0;
            int malicious = Get("malicious"), suspicious = Get("suspicious");
            int total = malicious + suspicious + Get("undetected") + Get("harmless");
            return new VirusTotalReport(true, malicious, suspicious, total, link,
                $"{malicious} / {total} engines flagged" + (suspicious > 0 ? $" ({suspicious} suspicious)" : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "VirusTotal lookup failed");
            return new VirusTotalReport(false, -1, 0, 0, link, "VirusTotal unavailable (" + ex.Message + ")");
        }
        finally { _queue.Release(); }
    }
}
