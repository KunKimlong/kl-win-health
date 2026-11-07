using System.IO;
using System.Text.RegularExpressions;
using Serilog;

namespace KLWinHealth.Services.VirusScan;

public record DefenderScanResult(bool Available, bool ThreatFound, Dictionary<string, string> Threats, string Message)
{
    /// <summary>Threats that aren't files (registry keys, processes) — only Windows Security can handle those.</summary>
    public int NonFileThreats { get; init; }
}

/// <summary>
/// Runs Microsoft Defender's command-line scanner (MpCmdRun.exe -Scan -ScanType 3 -File "path" -DisableRemediation)
/// with no console window. Exit code 2 = threat found. Works for single files and whole folders.
/// </summary>
public class DefenderScanner
{
    private static readonly Regex ThreatLine = new(@"^\s*Threat\s*:\s*(?<name>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ResourceLine = new(@"^\s*(?:file|containerfile)\s*:\s*(?<path>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private string? _mpCmdRun;
    private bool _located;

    public string? MpCmdRunPath
    {
        get
        {
            if (!_located) { _mpCmdRun = Locate(); _located = true; }
            return _mpCmdRun;
        }
    }

    /// <summary>Finds MpCmdRun.exe: newest %ProgramData%\Microsoft\Windows Defender\Platform\&lt;version&gt;\ first, then Program Files.</summary>
    private static string? Locate()
    {
        try
        {
            var platform = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender\Platform");
            if (Directory.Exists(platform))
            {
                var best = Directory.EnumerateDirectories(platform)
                    .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                    .Where(x => x.ver != null && File.Exists(Path.Combine(x.dir, "MpCmdRun.exe")))
                    .OrderByDescending(x => x.ver)
                    .FirstOrDefault();
                if (best.dir != null) return Path.Combine(best.dir, "MpCmdRun.exe");
            }
        }
        catch (Exception ex) { Log.Debug(ex, "Defender platform folder lookup failed"); }

        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
        return File.Exists(pf) ? pf : null;
    }

    public bool IsServiceRunning => ServiceControl.IsRunning("WinDefend");

    /// <summary>Scans one file or folder (custom scan, no remediation — the user decides what to quarantine).</summary>
    public Task<DefenderScanResult> ScanAsync(string path, CancellationToken ct) =>
        RunScanAsync($"-Scan -ScanType 3 -File \"{path.TrimEnd('\\')}\" -DisableRemediation", path, ct);

    /// <summary>
    /// Whole-PC scan. Quick = memory, startup items and common malware locations (minutes).
    /// Full = every file on every fixed drive (can take hours). No remediation: threats are listed for the user.
    /// </summary>
    public async Task<DefenderScanResult> ScanSystemAsync(bool full, CancellationToken ct)
    {
        try
        {
            return await RunScanAsync($"-Scan -ScanType {(full ? 2 : 1)} -DisableRemediation", full ? "full scan" : "quick scan", ct);
        }
        catch (OperationCanceledException)
        {
            // Killing MpCmdRun may leave the scan running inside the Defender service — ask it to stop too.
            if (MpCmdRunPath is { } exe)
            {
                try { await ProcessRunner.RunAsync(exe, "-Scan -Cancel", CancellationToken.None, timeout: TimeSpan.FromSeconds(30)); }
                catch (Exception ex) { Log.Debug(ex, "MpCmdRun -Scan -Cancel failed"); }
            }
            throw;
        }
    }

    private async Task<DefenderScanResult> RunScanAsync(string args, string path, CancellationToken ct)
    {
        var exe = MpCmdRunPath;
        if (exe == null) return new DefenderScanResult(false, false, new(), "Defender unavailable (MpCmdRun.exe not found)");
        if (!IsServiceRunning) return new DefenderScanResult(false, false, new(), "Defender unavailable (service not running)");

        ProcessResult r;
        try
        {
            r = await ProcessRunner.RunAsync(exe, args, ct, timeout: TimeSpan.FromHours(12));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "MpCmdRun failed for {Path}", path);
            return new DefenderScanResult(false, false, new(), "Defender unavailable (" + ex.Message + ")");
        }

        var output = r.Output + "\n" + r.Error;
        if (r.ExitCode == 0) return new DefenderScanResult(true, false, new(), "No threats found");

        if (r.ExitCode == 2)
        {
            // Map each "Threat : name" block to the file resources listed under it.
            var threats = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var blocks = output.Split("Threat information", StringSplitOptions.None);
            int nonFile = 0;
            foreach (var block in blocks.Skip(1))
            {
                var name = ThreatLine.Match(block) is { Success: true } m ? m.Groups["name"].Value : "Unknown threat";
                var any = false;
                foreach (Match res in ResourceLine.Matches(block))
                {
                    var p = res.Groups["path"].Value;
                    var arrow = p.IndexOf("->", StringComparison.Ordinal); // file inside an archive
                    if (arrow > 0) p = p[..arrow];
                    try { threats.TryAdd(Path.GetFullPath(p.Trim()), name); any = true; }
                    catch (Exception ex) { Log.Debug(ex, "Odd Defender resource path {Path}", p); }
                }
                if (!any) nonFile++; // registry / process / service resources
            }
            if (threats.Count == 0 && File.Exists(path))
                threats[Path.GetFullPath(path)] = ThreatLine.Match(output) is { Success: true } mm ? mm.Groups["name"].Value : "Threat detected";
            Log.Warning("Defender found {Count} threat file(s) (+{NonFile} non-file) in {Path}", threats.Count, nonFile, path);
            var summary = $"{threats.Count} threat(s) found" + (nonFile > 0 ? $" (+{nonFile} in registry/memory — see Windows Security)" : "");
            return new DefenderScanResult(true, true, threats, summary) { NonFileThreats = nonFile };
        }

        Log.Warning("MpCmdRun exit code {Code} for {Path}: {Output}", r.ExitCode, path, output.Trim());
        var msg = output.Contains("0x800106ba", StringComparison.OrdinalIgnoreCase) || output.Contains("not running", StringComparison.OrdinalIgnoreCase)
            ? "Defender unavailable (disabled or another antivirus is active)"
            : $"Defender scan failed (code {r.ExitCode})";
        return new DefenderScanResult(false, false, new(), msg);
    }
}
