using System.Diagnostics;
using System.Text;

namespace KLWinHealth.Services;

public record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Runs command-line tools with no console window, capturing output, honouring cancellation.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct = default,
        Action<string>? onOutputChunk = null, TimeSpan? timeout = null, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8
        };
        using var p = new Process { StartInfo = psi };
        p.Start();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // Read character blocks (not lines) so tools that redraw progress with '\r' (DISM) are visible live.
        var readOut = Task.Run(async () =>
        {
            var buf = new char[1024];
            int n;
            while ((n = await p.StandardOutput.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var chunk = new string(buf, 0, n);
                lock (stdout) stdout.Append(chunk);
                onOutputChunk?.Invoke(chunk);
            }
        });
        var readErr = Task.Run(async () =>
        {
            var s = await p.StandardError.ReadToEndAsync();
            lock (stderr) stderr.Append(s);
        });

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await p.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new System.TimeoutException($"{System.IO.Path.GetFileName(fileName)} timed out.");
        }
        await Task.WhenAll(readOut, readErr);
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
