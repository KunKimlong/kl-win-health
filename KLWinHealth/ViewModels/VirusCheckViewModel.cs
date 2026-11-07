using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Converters;
using KLWinHealth.Models;
using KLWinHealth.Services;
using KLWinHealth.Services.VirusScan;
using Serilog;

namespace KLWinHealth.ViewModels;

public partial class VirusCheckViewModel : PageViewModel, IFileDropTarget
{
    private readonly DefenderScanner _defender;
    private readonly VirusTotalClient _virusTotal;
    private readonly QuarantineService _quarantine;
    private readonly ICollectionView _view;

    public override string Title => "Virus check";

    public ObservableCollection<ScanResult> Results { get; } = new();
    public ObservableCollection<QuarantineRecord> QuarantineItems { get; } = new();
    public ICollectionView ResultsView => _view;

    [ObservableProperty] private ScanResult? _selectedResult;
    [ObservableProperty] private QuarantineRecord? _selectedQuarantine;
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _scannedCount;
    [ObservableProperty] private int _cleanCount;
    [ObservableProperty] private int _suspiciousCount;
    [ObservableProperty] private int _dangerousCount;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _defenderStatus = "";
    [ObservableProperty] private bool _defenderAvailable;
    [ObservableProperty] private string _virusTotalStatus = "";
    [ObservableProperty] private bool _showQuarantine;
    [ObservableProperty] private bool _hasScanned;

    public VirusCheckViewModel(PageContext ctx, DefenderScanner defender, VirusTotalClient virusTotal, QuarantineService quarantine) : base(ctx)
    {
        _defender = defender;
        _virusTotal = virusTotal;
        _quarantine = quarantine;
        _view = CollectionViewSource.GetDefaultView(Results);
        _view.Filter = o => o is ScanResult r && MatchesFilter(r);
        _view.SortDescriptions.Add(new SortDescription(nameof(ScanResult.Score), ListSortDirection.Descending));
    }

    public override Task OnNavigatedToAsync()
    {
        DefenderAvailable = _defender.MpCmdRunPath != null && _defender.IsServiceRunning;
        DefenderStatus = DefenderAvailable ? "Microsoft Defender ready" : "Defender unavailable";
        VirusTotalStatus = string.IsNullOrEmpty(Settings.GetVirusTotalKey()) ? "VirusTotal off (add an API key in Settings)" : "VirusTotal on (4 lookups/min)";
        LoadQuarantine();
        return Task.CompletedTask;
    }

    private bool MatchesFilter(ScanResult r)
    {
        if (Filter != "All" && r.Verdict.ToString() != Filter) return false;
        if (!string.IsNullOrWhiteSpace(Search) &&
            !r.FullPath.Contains(Search, StringComparison.OrdinalIgnoreCase) &&
            !r.Summary.Contains(Search, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    partial void OnFilterChanged(string value) => _view.Refresh();
    partial void OnSearchChanged(string value) => _view.Refresh();

    // ------------------------------------------------------------------ scanning
    public void OnFilesDropped(string[] paths) => _ = ScanPathsAsync(paths);

    [RelayCommand]
    private async Task ScanFileAsync()
    {
        var files = Dialogs.PickFiles("Choose file(s) to scan", "All files (*.*)|*.*");
        if (files is { Length: > 0 }) await ScanPathsAsync(files);
    }

    [RelayCommand]
    private async Task ScanFolderAsync()
    {
        var folder = Dialogs.PickFolder("Choose a folder to scan (sub-folders included)");
        if (folder != null) await ScanPathsAsync(new[] { folder });
    }

    public async Task ScanPathsAsync(string[] inputs)
    {
        if (Busy.IsBusy)
        {
            Dialogs.Toast("A scan is already running.", ToastKind.Warning);
            return;
        }
        Results.Clear();
        SelectedResult = null;
        HasScanned = true;
        UpdateCounts();
        var sw = Stopwatch.StartNew();

        await RunBusyAsync("Collecting files…", async ct =>
        {
            // 1. Collect files
            var files = await Task.Run(() => CollectFiles(inputs, ct), ct);
            if (files.Count == 0)
            {
                Dialogs.Toast("No files found to scan.", ToastKind.Warning);
                return;
            }
            Log.Information("Virus check: scanning {Count} file(s) from {Inputs}", files.Count, string.Join("; ", inputs));
            var byPath = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var r = new ScanResult(f) { DefenderResult = DefenderAvailable ? "Scanning…" : "Defender unavailable" };
                byPath[f] = r;
                Results.Add(r);
            }

            // 2. Defender (one run per dropped/picked item; folders are scanned recursively by Defender itself)
            var defenderTask = DefenderAvailable
                ? Task.WhenAll(inputs.Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => _defender.ScanAsync(Path.GetFullPath(p), ct)))
                : Task.FromResult(Array.Empty<DefenderScanResult>());

            // 3. Heuristics in parallel
            await RunHeuristicsAsync(files, byPath, 90, "Analyzing", ct);

            // 4. Defender results
            if (DefenderAvailable)
            {
                Busy.Report(-1, "Waiting for Microsoft Defender…");
                var defResults = await defenderTask;
                var threats = defResults.SelectMany(d => d.Threats).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                var unavailable = defResults.FirstOrDefault(d => !d.Available);
                foreach (var r in Results)
                {
                    if (threats.TryGetValue(Path.GetFullPath(r.FullPath), out var name))
                    {
                        r.DefenderThreat = name;
                        r.DefenderResult = "Threat: " + name;
                    }
                    else r.DefenderResult = unavailable?.Message ?? "No threats found";
                    r.Recalculate();
                }
            }
            foreach (var r in Results) r.Recalculate();

            sw.Stop();
            UpdateCounts();
            ElapsedText = Format.Duration(sw.Elapsed);
            _view.Refresh();
            SaveThreatStat();
            Log.Information("Virus check done: {Count} files, {Clean} clean, {Susp} suspicious, {Danger} dangerous in {Elapsed}",
                ScannedCount, CleanCount, SuspiciousCount, DangerousCount, ElapsedText);

            // 5. VirusTotal (optional, rate-limited, runs last so local results are already visible)
            await VirusTotalPhaseAsync(ct);
            UpdateCounts();
            _view.Refresh();
            SaveThreatStat();
        }, errorTitle: "Virus check");
        if (sw.IsRunning) { sw.Stop(); ElapsedText = Format.Duration(sw.Elapsed); }
        UpdateCounts();
    }

    /// <summary>Runs the local heuristics on every file in parallel; progress goes from 0 to <paramref name="progressSpan"/>.</summary>
    private async Task RunHeuristicsAsync(IReadOnlyList<string> files, Dictionary<string, ScanResult> byPath, double progressSpan, string verb, CancellationToken ct)
    {
        int done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6), CancellationToken = ct },
            async (f, token) =>
            {
                var r = byPath[f];
                await AnalyzeOneAsync(r, token);
                var n = Interlocked.Increment(ref done);
                if (n % 5 == 0 || n == files.Count)
                    Busy.Report(n * progressSpan / files.Count, $"{verb} {n:N0} / {files.Count:N0}: {Path.GetFileName(f)}");
            });
    }

    private static async Task AnalyzeOneAsync(ScanResult r, CancellationToken ct)
    {
        try { await HeuristicAnalyzer.AnalyzeAsync(r.FullPath, r, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug(ex, "Heuristics failed for {Path}", r.FullPath);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                r.Error = ex is UnauthorizedAccessException ? "Access denied — content skipped"
                    : ex is FileNotFoundException ? "File disappeared during the scan (possibly removed by Defender)"
                    : ex is IOException ? "File locked or unreadable — content skipped" : ex.Message;
                r.FileType = "—";
                r.Findings.Clear();
                foreach (var nf in HeuristicAnalyzer.NameFindings(r.FileName)) r.Findings.Add(nf);
                r.Findings.Add(new Finding(r.Error, 0, "Info"));
                r.Recalculate();
            });
        }
    }

    // ------------------------------------------------------------------ whole PC
    private static readonly HashSet<string> DocumentAndArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docm", ".docx", ".dot", ".dotm", ".xls", ".xlsm", ".xlsx", ".xlsb", ".xlam", ".ppt", ".pptm", ".pptx", ".rtf",
        ".zip", ".rar", ".7z", ".iso", ".img", ".vhd", ".vhdx", ".cab", ".lnk", ".url", ".hta", ".chm"
    };

    private const long MaxHotspotFileSize = 150L * 1024 * 1024;
    private const int MaxHotspotFiles = 20000;

    /// <summary>
    /// Where malware usually lands: Downloads, Desktop, Temp, AppData, Startup and ProgramData.
    /// Only risky types (programs, scripts, Office/PDF documents, archives, shortcuts) are checked locally;
    /// Defender covers everything else.
    /// </summary>
    private static List<string> CollectHotspotFiles(CancellationToken ct)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // (folder, max depth, executables/scripts only?)
        var spots = new List<(string? dir, int depth, bool codeOnly)>
        {
            (StorageService.DownloadsFolder, 6, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 4, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), 2, false),
            (Path.GetTempPath(), 4, false),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"), 3, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), 2, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), 2, false),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 4, true),
            (local, 2, true),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 2, true),
            (Path.Combine(profile, "Public"), 3, true)
        };

        var found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, depth, codeOnly) in spots)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
            };
            try
            {
                foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = fi.Extension;
                    var code = FileTypeDetector.ExecutableExtensions.Contains(ext) || FileTypeDetector.ScriptExtensions.Contains(ext);
                    if (!code && (codeOnly || !DocumentAndArchiveExtensions.Contains(ext))) continue;
                    try
                    {
                        if (fi.Length > MaxHotspotFileSize) continue;
                        found.TryAdd(fi.FullName, fi.LastWriteTimeUtc);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug(ex, "Hotspot enumeration failed in {Dir}", dir); }
        }
        // Newest first; cap the list so a huge AppData can't make the scan take forever (Defender still scans everything).
        return found.OrderByDescending(kv => kv.Value).Take(MaxHotspotFiles).Select(kv => kv.Key).ToList();
    }

    /// <summary>Scan entire PC. Parameter: "Quick" or "Full".</summary>
    [RelayCommand]
    private async Task ScanPcAsync(string? mode)
    {
        var full = string.Equals(mode, "Full", StringComparison.OrdinalIgnoreCase);
        var kind = full ? "Full scan" : "Quick scan";
        if (Busy.IsBusy) { Dialogs.Toast("A scan is already running.", ToastKind.Warning); return; }

        DefenderAvailable = _defender.MpCmdRunPath != null && _defender.IsServiceRunning;
        var details = new List<string>
        {
            full ? "Microsoft Defender scans every file on every drive. This can take 1–3 hours."
                 : "Microsoft Defender checks memory, startup items and common malware locations. Usually 5–15 minutes.",
            "KL Win Health also checks programs, scripts, documents and archives in Downloads, Desktop, Temp, AppData, Startup and ProgramData.",
            "Nothing is deleted or changed — you choose what to quarantine afterwards.",
            "You can keep using the PC. Cancel stops the scan at any time."
        };
        if (!DefenderAvailable)
            details.Insert(0, "Microsoft Defender is unavailable (turned off or another antivirus is active), so only KL Win Health's own checks will run.");
        if (!await Dialogs.ConfirmAsync($"{kind} of this PC?", full ? "Scan the entire PC, every file on every drive?" : "Run a quick scan of this PC?",
                details, $"Start {kind.ToLowerInvariant()}")) return;

        Results.Clear();
        SelectedResult = null;
        HasScanned = true;
        UpdateCounts();
        var sw = Stopwatch.StartNew();
        int nonFileThreats = 0;

        var ok = await RunBusyAsync($"{kind}: starting…", async ct =>
        {
            Log.Information("Virus check: {Kind} of the PC started (Defender available={Def})", kind, DefenderAvailable);

            // 1. Defender system scan runs in the background the whole time.
            async Task<DefenderScanResult?> RunDefender() => await _defender.ScanSystemAsync(full, ct);
            var defTask = DefenderAvailable ? RunDefender() : Task.FromResult<DefenderScanResult?>(null);

            // 2. Our heuristics on the usual malware locations.
            Busy.Report(-1, $"{kind}: collecting files in risky locations…");
            var files = await Task.Run(() => CollectHotspotFiles(ct), ct);
            var byPath = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var r = new ScanResult(f) { DefenderResult = DefenderAvailable ? $"Waiting for {kind.ToLowerInvariant()}…" : "Defender unavailable" };
                byPath[f] = r;
                Results.Add(r);
            }
            UpdateCounts();
            Log.Information("Virus check: {Count} file(s) in risky locations", files.Count);
            await RunHeuristicsAsync(files, byPath, DefenderAvailable ? 50 : 90, $"{kind}: checking risky locations", ct);
            foreach (var r in Results) r.Recalculate();
            UpdateCounts();
            _view.Refresh();

            // 3. Wait for Defender, showing elapsed time (MpCmdRun reports no progress).
            if (DefenderAvailable)
            {
                while (!defTask.IsCompleted)
                {
                    Busy.Report(-1, $"Microsoft Defender {kind.ToLowerInvariant()} running… {Format.Duration(sw.Elapsed)} elapsed" +
                                    (full ? " (a full scan can take hours)" : ""));
                    await Task.WhenAny(defTask, Task.Delay(1000, ct));
                    ct.ThrowIfCancellationRequested();
                }
                var d = await defTask;
                if (d != null)
                {
                    nonFileThreats = d.NonFileThreats;
                    foreach (var (path, name) in d.Threats)
                    {
                        if (!byPath.TryGetValue(path, out var r))
                        {
                            // A threat outside the risky locations: add it and analyze it too.
                            r = new ScanResult(path);
                            byPath[path] = r;
                            Results.Add(r);
                            if (File.Exists(path)) await AnalyzeOneAsync(r, ct);
                        }
                        r.DefenderThreat = name;
                        r.DefenderResult = "Threat: " + name;
                    }
                    var cleanText = d.Available ? $"No threats found ({kind.ToLowerInvariant()})" : d.Message;
                    foreach (var r in Results.Where(r => r.DefenderThreat == null)) r.DefenderResult = cleanText;
                    Log.Information("Defender {Kind}: {Message}", kind, d.Message);
                }
            }
            foreach (var r in Results) r.Recalculate();

            sw.Stop();
            UpdateCounts();
            ElapsedText = Format.Duration(sw.Elapsed);
            _view.Refresh();
            SaveThreatStat();
            Log.Information("{Kind} done: {Count} files checked locally, {Clean} clean, {Susp} suspicious, {Danger} dangerous in {Elapsed}",
                kind, ScannedCount, CleanCount, SuspiciousCount, DangerousCount, ElapsedText);

            // 4. VirusTotal for anything risky (optional).
            await VirusTotalPhaseAsync(ct);
            UpdateCounts();
            _view.Refresh();
            SaveThreatStat();
        }, errorTitle: kind);
        if (sw.IsRunning) { sw.Stop(); ElapsedText = Format.Duration(sw.Elapsed); }
        UpdateCounts();

        if (nonFileThreats > 0)
        {
            await Dialogs.InfoAsync("Threats outside files",
                $"Microsoft Defender also found {nonFileThreats} threat(s) in the registry, memory or services. These can't be quarantined here — open Windows Security → Virus & threat protection → Protection history to remove them.");
            ShellHelper.OpenUrl("windowsdefender://threathistory");
        }
        else if (ok && DangerousCount == 0 && SuspiciousCount == 0)
            Dialogs.Toast($"{kind} finished — no threats found.", ToastKind.Success);
    }

    private static List<string> CollectFiles(string[] inputs, CancellationToken ct)
    {
        var list = new List<string>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(input)) list.Add(Path.GetFullPath(input));
            else if (Directory.Exists(input))
            {
                foreach (var f in Directory.EnumerateFiles(input, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    list.Add(f);
                }
            }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task VirusTotalPhaseAsync(CancellationToken ct)
    {
        var key = Settings.GetVirusTotalKey();
        if (string.IsNullOrEmpty(key)) return;

        // Look up risky types and anything already flagged; skip the rest to respect the 4/min quota.
        var candidates = Results.Where(r => r.Sha256 != null && r.Error == null &&
                (r.Score >= 30 || IsRiskyType(r))).OrderByDescending(r => r.Score).ToList();
        foreach (var r in Results.Except(candidates)) r.VirusTotalResult = "Not checked (low-risk type)";
        for (int i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = candidates[i];
            Busy.Report(90 + i * 10.0 / candidates.Count, $"VirusTotal {i + 1} / {candidates.Count}: {r.FileName}");
            r.VirusTotalResult = "Checking…";
            var rep = await _virusTotal.LookupAsync(r.Sha256!, key, ct, msg => Busy.Report(-1, msg));
            r.VirusTotalResult = rep.Message;
            r.VirusTotalLink = rep.Link;
            r.VirusTotalMalicious = rep.Found ? rep.Malicious : -1;
            r.Recalculate();
        }
    }

    private static bool IsRiskyType(ScanResult r)
    {
        var ext = Path.GetExtension(r.FullPath);
        return FileTypeDetector.ExecutableExtensions.Contains(ext) || FileTypeDetector.ScriptExtensions.Contains(ext) ||
               r.FileType is "Windows executable" or "PDF document" or "ZIP archive" or "RAR archive" or "7-Zip archive" ||
               r.FileType.StartsWith("Word", StringComparison.Ordinal) || r.FileType.StartsWith("Excel", StringComparison.Ordinal) ||
               r.FileType.StartsWith("PowerPoint", StringComparison.Ordinal);
    }

    private void UpdateCounts()
    {
        ScannedCount = Results.Count;
        CleanCount = Results.Count(r => r.Verdict == Verdict.Clean);
        SuspiciousCount = Results.Count(r => r.Verdict == Verdict.Suspicious);
        DangerousCount = Results.Count(r => r.Verdict == Verdict.Dangerous && !r.IsQuarantined);
    }

    private void SaveThreatStat()
    {
        Settings.Current.LastScanThreats = Results.Count(r => r.Verdict == Verdict.Dangerous && !r.IsQuarantined);
        Settings.Current.LastScanTime = DateTime.Now;
        Settings.Save();
    }

    // ------------------------------------------------------------------ actions
    [RelayCommand]
    private async Task QuarantineAsync(IList? selected)
    {
        var items = (selected?.Cast<ScanResult>() ?? (SelectedResult != null ? new[] { SelectedResult } : Array.Empty<ScanResult>()))
            .Where(r => !r.IsQuarantined).ToList();
        if (items.Count == 0) return;
        if (!await Dialogs.ConfirmAsync("Quarantine",
                $"Move {items.Count} file(s) to quarantine? They are moved to {AppPaths.Quarantine} and can't run. You can restore them later.",
                items.Select(r => $"{r.FullPath}  [{r.Verdict}]"), "Quarantine", danger: true)) return;

        var errors = new List<string>();
        foreach (var r in items)
        {
            try
            {
                _quarantine.Quarantine(r.FullPath, r.Sha256, r.Summary);
                r.IsQuarantined = true;
                r.DefenderResult = r.DefenderResult; // keep
                r.Summary = "Quarantined · " + r.Summary;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Quarantine failed for {Path}", r.FullPath);
                errors.Add($"{r.FileName}: {FriendlyError(ex)}");
            }
        }
        UpdateCounts();
        SaveThreatStat();
        LoadQuarantine();
        if (errors.Count > 0) await Dialogs.ErrorAsync("Quarantine", "Some files could not be quarantined.", errors);
        else Dialogs.Toast($"{items.Count} file(s) quarantined.", ToastKind.Success);
    }

    private void LoadQuarantine()
    {
        QuarantineItems.Clear();
        foreach (var q in _quarantine.List()) QuarantineItems.Add(q);
    }

    [RelayCommand] private void ToggleQuarantine() { LoadQuarantine(); ShowQuarantine = !ShowQuarantine; }

    [RelayCommand]
    private async Task RestoreFromQuarantineAsync()
    {
        if (SelectedQuarantine is not { } q) return;
        if (!await Dialogs.ConfirmAsync("Restore from quarantine",
                $"Put {q.FileName} back to its original location? Only do this if you're sure the file is safe.",
                new[] { q.OriginalPath, "Reason: " + q.Reason }, "Restore", danger: true)) return;
        try
        {
            var dest = _quarantine.Restore(q);
            var r = Results.FirstOrDefault(x => string.Equals(x.FullPath, q.OriginalPath, StringComparison.OrdinalIgnoreCase));
            if (r != null) { r.IsQuarantined = false; r.Recalculate(); }
            Dialogs.Toast("Restored to " + dest, ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restore from quarantine failed");
            await Dialogs.ErrorAsync("Restore from quarantine", FriendlyError(ex));
        }
        LoadQuarantine();
        UpdateCounts();
    }

    [RelayCommand]
    private async Task DeleteFromQuarantineAsync()
    {
        if (SelectedQuarantine is not { } q) return;
        if (!await Dialogs.ConfirmAsync("Delete permanently", $"Permanently delete {q.FileName} from quarantine?",
                new[] { q.OriginalPath }, "Delete", danger: true, warning: "This can't be undone.")) return;
        try { _quarantine.DeletePermanently(q); }
        catch (Exception ex) { await Dialogs.ErrorAsync("Delete", FriendlyError(ex)); }
        LoadQuarantine();
    }

    [RelayCommand]
    private void OpenLocation()
    {
        if (SelectedResult is { } r) ShellHelper.ShowInExplorer(r.FullPath);
    }

    [RelayCommand]
    private void CopySha()
    {
        if (SelectedResult?.Sha256 is not { } sha) return;
        try { Clipboard.SetText(sha); Dialogs.Toast("SHA-256 copied."); }
        catch (Exception ex) { Log.Warning(ex, "Clipboard failed"); }
    }

    [RelayCommand]
    private void OpenVirusTotal()
    {
        var link = SelectedResult?.VirusTotalLink ?? (SelectedResult?.Sha256 is { } s ? $"https://www.virustotal.com/gui/file/{s}" : null);
        if (link != null) ShellHelper.OpenUrl(link);
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Results.Count == 0) return;
        var file = Dialogs.SaveFile("Export scan report", "CSV file (*.csv)|*.csv", $"KLWinHealth_scan_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        if (file == null) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Verdict,Score,File name,Full path,Type,Size,SHA-256,Signer,Defender,VirusTotal,Findings,Quarantined");
            foreach (var r in Results.OrderByDescending(r => r.Score))
            {
                string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
                sb.AppendLine(string.Join(",", r.Verdict, r.Score, Q(r.FileName), Q(r.FullPath), Q(r.FileType), r.Size, Q(r.Sha256),
                    Q(r.Signer), Q(r.DefenderResult), Q(r.VirusTotalResult), Q(string.Join(" | ", r.Findings.Select(f => f.Display))), r.IsQuarantined));
            }
            await File.WriteAllTextAsync(file, sb.ToString(), new UTF8Encoding(true));
            Dialogs.Toast("Report exported.", ToastKind.Success);
            ShellHelper.ShowInExplorer(file);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CSV export failed");
            await Dialogs.ErrorAsync("Export", FriendlyError(ex));
        }
    }

    [RelayCommand]
    private void ClearResults()
    {
        if (Busy.IsBusy) return;
        Results.Clear();
        HasScanned = false;
        ElapsedText = "";
        UpdateCounts();
    }
}
