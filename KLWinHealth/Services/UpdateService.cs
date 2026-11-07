using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using KLWinHealth.Services.VirusScan;
using Serilog;

namespace KLWinHealth.Services;

public record UpdateInfo(Version Version, string Tag, string ExeUrl, string? Sha256Url, string ReleaseUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer version, downloads it, verifies it and hands over to it.
///
/// A release must have the tag vX.Y.Z and two assets made by build.bat: KLWinHealth.exe and KLWinHealth.exe.sha256.
/// Verification: HTTPS download, SHA-256 must match the .sha256 asset, and if this build is code-signed the update
/// must carry a valid signature from the same signer (so a hijacked release can't push an unsigned/foreign exe).
/// </summary>
public class UpdateService
{
    // ---- Where releases are published -------------------------------------------------------------
    public const string GitHubOwner = "KunKimlong";
    public const string GitHubRepo = "kl-win-health";
    public static bool IsConfigured => GitHubOwner.Length > 0 && GitHubRepo.Length > 0;
    public static string ReleasesPage => $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases";

    private const string ExeAsset = "KLWinHealth.exe";
    private const string ShaAsset = "KLWinHealth.exe.sha256";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static Version CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version ?? new Version(1, 0, 0);
    public UpdateInfo? Available { get; private set; }
    public event Action<UpdateInfo>? UpdateAvailable;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"KLWinHealth-Updater/{CurrentVersion.ToString(3)}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Background check at startup and then daily, if enabled. Never throws.</summary>
    public async Task RunBackgroundChecksAsync(CancellationToken ct)
    {
        if (!IsConfigured) return;
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            var s = _settings.Current;
            if (s.CheckForUpdates && (s.LastUpdateCheck is not { } last || DateTime.UtcNow - last >= CheckInterval))
            {
                try { await CheckAsync(ct); }
                catch (Exception ex) { Log.Information("Update check failed: {Msg}", ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { return; }
        }
    }

    /// <summary>Asks GitHub for the latest release. Returns it if newer than this build, else null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("Updates are not set up for this build.");
        await _gate.WaitAsync(ct);
        try
        {
            var url = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                MarkChecked();
                return null; // no releases published yet
            }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            MarkChecked();

            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            {
                Log.Warning("Latest release tag {Tag} is not a version number", tag);
                return null;
            }
            if (Normalize(version) <= Normalize(CurrentVersion))
            {
                Log.Information("Up to date ({Current}; latest release {Latest})", CurrentVersion.ToString(3), tag);
                Available = null;
                return null;
            }

            string? exeUrl = null, shaUrl = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var dl = a.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) exeUrl = dl;
                else if (string.Equals(name, ShaAsset, StringComparison.OrdinalIgnoreCase)) shaUrl = dl;
            }
            if (exeUrl == null)
            {
                Log.Warning("Release {Tag} has no {Asset} asset", tag, ExeAsset);
                return null;
            }

            var info = new UpdateInfo(version, tag, exeUrl, shaUrl,
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
            Available = info;
            Log.Information("Update available: {Tag}", tag);
            UpdateAvailable?.Invoke(info);
            return info;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Downloads and verifies the update. Returns the path of the verified exe.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.Root, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch { } }

        var target = Path.Combine(dir, $"KLWinHealth-{info.Version.ToString(3)}.exe");
        var partial = target + ".part";

        using (var resp = await _http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(done * 100.0 / total.Value);
            }
        }

        try
        {
            await VerifyAsync(info, partial, ct);
        }
        catch
        {
            try { File.Delete(partial); } catch { }
            throw;
        }
        File.Move(partial, target, overwrite: true);
        Log.Information("Update {Tag} downloaded and verified: {Path}", info.Tag, target);
        return target;
    }

    private async Task VerifyAsync(UpdateInfo info, string file, CancellationToken ct)
    {
        // 1) Checksum published next to the exe.
        if (info.Sha256Url != null)
        {
            var expected = (await _http.GetStringAsync(info.Sha256Url, ct)).Trim().Split(' ', '\t', '\r', '\n')[0].Replace(" ", "");
            await using var fs = File.OpenRead(file);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The downloaded update is damaged or was altered (SHA-256 mismatch). Nothing was installed.");
        }

        // 2) Same publisher. Once builds are signed, unsigned or differently-signed updates are refused.
        var mine = SignatureVerifier.Verify(AppPaths.ExePath);
        var theirs = SignatureVerifier.Verify(file);
        if (mine.Status == SignatureStatus.Valid)
        {
            if (theirs.Status != SignatureStatus.Valid || !string.Equals(theirs.Signer, mine.Signer, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The update is not signed by {mine.Signer} (found: {theirs.Signer ?? theirs.Status.ToString()}). Nothing was installed.");
        }
        else if (info.Sha256Url == null)
        {
            throw new InvalidOperationException("The release has no checksum file and this build is not signed, so the update can't be verified.");
        }

        // 3) Really is the version the release claims.
        var v = FileVersionInfo.GetVersionInfo(file);
        var fileVersion = new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        if (Normalize(fileVersion) != Normalize(info.Version))
            throw new InvalidOperationException($"The release {info.Tag} contains version {fileVersion.ToString(3)}. Nothing was installed.");
    }

    /// <summary>Starts the verified exe. It waits for this process to exit, installs itself, then reopens the app.</summary>
    public static void LaunchInstaller(string exe)
    {
        var pid = Environment.ProcessId;
        Process.Start(new ProcessStartInfo(exe, $"--wait-exit={pid} --updated")
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        Log.Information("Started update installer {Exe}", exe);
    }

    private void MarkChecked()
    {
        _settings.Current.LastUpdateCheck = DateTime.UtcNow;
        _settings.Save();
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
