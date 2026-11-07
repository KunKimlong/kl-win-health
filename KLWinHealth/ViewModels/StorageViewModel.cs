using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Converters;
using KLWinHealth.Services;
using Serilog;

namespace KLWinHealth.ViewModels;

/// <summary>One fixed drive with its usage bar.</summary>
public class DriveUsage
{
    public string Root { get; init; } = "";
    public string Label { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public long Used => Total - Free;
    public double UsedPercent => Total > 0 ? 100.0 * Used / Total : 0;
    public bool IsLow => Total > 0 && Free < Total * 0.10;
    public string Title => string.IsNullOrWhiteSpace(Label) ? Root.TrimEnd('\\') : $"{Label} ({Root.TrimEnd('\\')})";
    public string UsageText => $"{Format.Bytes(Free)} free of {Format.Bytes(Total)}";
    public override string ToString() => Title;
}

/// <summary>A file row with a checkbox (large file finder / Downloads review).</summary>
public partial class FileRow : ObservableObject
{
    public LargeFile File { get; }
    public string Name => File.Name;
    public string Folder => File.Folder;
    public string FullPath => File.FullPath;
    public long Size => File.Size;
    public DateTime LastModified => File.LastModified;
    public string AgeText
    {
        get
        {
            var days = (DateTime.Now - File.LastModified).TotalDays;
            return File.LastModified == DateTime.MinValue ? "—" : days < 1 ? "today" : days < 60 ? $"{days:0} days" : days < 730 ? $"{days / 30:0} months" : $"{days / 365:0.#} years";
        }
    }

    [ObservableProperty] private bool _isChecked;
    public FileRow(LargeFile f) => File = f;
}

/// <summary>Module 5 — Clean storage: categories, large files, old downloads.</summary>
public partial class StorageViewModel : PageViewModel
{
    private readonly StorageService _storage;
    private readonly SystemStatsService _stats;

    public override string Title => "Clean storage";
    public override string Subtitle => "Free up disk space. Safe items are pre-selected; personal folders are never cleaned automatically.";

    public ObservableCollection<DriveUsage> Drives { get; } = new();
    public ObservableCollection<CleanupCategory> Categories { get; } = new();
    public ObservableCollection<FileRow> LargeFiles { get; } = new();
    public ObservableCollection<FileRow> OldDownloads { get; } = new();
    public int[] DownloadAgeOptions { get; } = { 30, 90, 180, 365 };

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isAnalyzed;
    [ObservableProperty] private string _selectedSummary = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private DriveUsage? _largeFilesDrive;
    [ObservableProperty] private string _largeFilesSummary = "Pick a drive and click Scan to list its 100 largest files.";
    [ObservableProperty] private int _downloadAge = 90;
    [ObservableProperty] private string _downloadsSummary = "";
    [ObservableProperty] private CleanupCategory? _selectedCategory;

    public string DownloadsFolder => StorageService.DownloadsFolder ?? "Downloads";

    public StorageViewModel(PageContext ctx, StorageService storage, SystemStatsService stats) : base(ctx)
    {
        _storage = storage;
        _stats = stats;
        foreach (var c in _storage.CreateCategories())
        {
            c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CleanupCategory.IsChecked) or nameof(CleanupCategory.Size)) UpdateSelectedSummary();
            };
            Categories.Add(c);
        }
        UpdateSelectedSummary();
    }

    public override Task OnNavigatedToAsync()
    {
        LoadDrives();
        return Task.CompletedTask;
    }

    private void LoadDrives()
    {
        var keep = LargeFilesDrive?.Root;
        Drives.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                Drives.Add(new DriveUsage { Root = d.RootDirectory.FullName, Label = d.VolumeLabel, Total = d.TotalSize, Free = d.AvailableFreeSpace });
            }
            catch (Exception ex) { Log.Debug(ex, "Drive {Drive} unreadable", d.Name); }
        }
        var sys = Path.GetPathRoot(Environment.SystemDirectory);
        LargeFilesDrive = Drives.FirstOrDefault(d => d.Root == keep) ??
                          Drives.FirstOrDefault(d => string.Equals(d.Root, sys, StringComparison.OrdinalIgnoreCase)) ?? Drives.FirstOrDefault();
    }

    private void UpdateSelectedSummary()
    {
        var sel = Categories.Where(c => c.IsChecked && !c.IsShortcut).ToList();
        var known = sel.Where(c => c.Size > 0).Sum(c => c.Size);
        SelectedSummary = sel.Count == 0
            ? "Nothing selected"
            : IsAnalyzed ? $"{sel.Count} selected · {Format.Bytes(known)} to free" + (sel.Any(c => c.Def.Kind == "Dism") ? " + DISM" : "")
                         : $"{sel.Count} selected · click Analyze to see sizes";
    }

    // ------------------------------------------------------------------ analyze / clean
    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        ResultText = "";
        await RunBusyAsync("Analyzing…", async ct =>
        {
            var list = Categories.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Busy.Report(100.0 * i / list.Count, $"Analyzing {list[i].Name}…");
                try { await _storage.AnalyzeAsync(list[i], ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Analyze {Id} failed", list[i].Def.Id);
                    list[i].Status = "Couldn't analyze";
                }
            }
        });
        // Size updates are posted to the UI thread; let them land before summarizing.
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        IsAnalyzed = Categories.Any(c => c.IsAnalyzed);
        UpdateSelectedSummary();
        LoadDrives();
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        var selected = Categories.Where(c => c.IsChecked && !c.IsShortcut).ToList();
        if (selected.Count == 0) { Dialogs.Toast("Select at least one category."); return; }
        if (!IsAnalyzed)
        {
            await AnalyzeAsync();
            if (!IsAnalyzed) return;
        }
        // Skip categories already known to be empty (but keep Dism / not-analyzable ones).
        selected = selected.Where(c => c.Size != 0 || c.Def.Kind == "Dism").ToList();
        if (selected.Count == 0) { Dialogs.Toast("The selected categories are already clean.", ToastKind.Success); return; }
        if (!App.IsAdmin && selected.Any(c => c.Def.Kind is "WindowsUpdate" or "WindowsOld" or "Dism"))
        {
            await Dialogs.InfoAsync("Administrator needed", "Windows Update cache, Windows.old and DISM cleanup need administrator rights. Restart KL Win Health as administrator, or untick them.");
            return;
        }

        var details = selected.Select(c => $"{c.Name}: {(c.Size > 0 ? Format.Bytes(c.Size) : c.Def.Kind == "Dism" ? "size calculated by DISM" : "—")}").ToList();
        var caution = selected.Where(c => c.IsCaution).ToList();
        string? warning = "These files can't be restored.";
        if (caution.Count > 0)
            warning += " Caution items: " + string.Join("; ", caution.Select(c => c.Name)) + ". Read their descriptions first.";
        if (!await Dialogs.ConfirmAsync("Clean selected items?",
                $"Delete about {Format.Bytes(selected.Where(c => c.Size > 0).Sum(c => c.Size))}?", details,
                "Clean now", danger: caution.Count > 0, warning: warning)) return;

        var total = CleanResult.Empty;
        var perCategory = new List<string>();
        var ok = await RunBusyAsync("Cleaning…", async ct =>
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var c = selected[i];
                ct.ThrowIfCancellationRequested();
                var baseP = 100.0 * i / selected.Count;
                Busy.Report(baseP, $"Cleaning {c.Name}…");
                try
                {
                    var r = await _storage.CleanAsync(c, (p, s) => Busy.Report(baseP + p / selected.Count, $"{c.Name}: {s}"), ct);
                    total = total.Add(r);
                    perCategory.Add($"{c.Name}: {Format.Bytes(r.BytesFreed)} freed" + (r.FilesSkipped > 0 ? $", {r.FilesSkipped:N0} in use (skipped)" : ""));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error(ex, "Cleaning {Id} failed", c.Def.Id);
                    perCategory.Add($"{c.Name}: failed — {FriendlyError(ex)}");
                }
            }
        });

        _stats.RefreshDiskNow();
        ResultText = $"Last clean: freed {Format.Bytes(total.BytesFreed)}" + (total.FilesSkipped > 0 ? $" · {total.FilesSkipped:N0} files in use were skipped" : "");
        await AnalyzeAsync();
        if (ok)
            await Dialogs.SuccessAsync("Cleaning finished", $"Freed {Format.Bytes(total.BytesFreed)}.", perCategory.Concat(total.Notes));
        else if (perCategory.Count > 0)
            await Dialogs.InfoAsync("Cleaning stopped", $"Freed {Format.Bytes(total.BytesFreed)} before stopping.", perCategory);
    }

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var c in Categories) c.IsChecked = !c.IsCaution;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var c in Categories) c.IsChecked = false;
    }

    [RelayCommand]
    private void OpenBrowserCleaner()
    {
        Ctx.Navigation.NavigateTo<BrowserCleanerViewModel>();
        if (App.Services.GetService(typeof(BrowserCleanerViewModel)) is BrowserCleanerViewModel b) b.SelectedTab = 3;
    }

    [RelayCommand]
    private void OpenCategoryFolder(CleanupCategory? c)
    {
        var p = c?.Def.Paths.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(Directory.Exists);
        if (p != null) ShellHelper.OpenFolder(p);
        else Dialogs.Toast("This item has no folder to open.");
    }

    // ------------------------------------------------------------------ large files
    [RelayCommand]
    private async Task ScanLargeFilesAsync()
    {
        var d = LargeFilesDrive;
        if (d == null) return;
        List<LargeFile> list = new();
        await RunBusyAsync($"Scanning {d.Root}…", async ct =>
        {
            list = await _storage.FindLargestFilesAsync(d.Root, 100, n => Busy.Report(-1, $"Scanning {d.Root}… {n:N0} files checked"), ct);
        });
        LargeFiles.Clear();
        foreach (var f in list) LargeFiles.Add(new FileRow(f));
        LargeFilesSummary = list.Count == 0 ? "No files found." : $"{list.Count} largest files on {d.Root} · {Format.Bytes(list.Sum(f => f.Size))} in total";
    }

    [RelayCommand]
    private void BrowseLargeFilesFolder()
    {
        var folder = Dialogs.PickFolder("Pick a folder to scan for large files");
        if (folder == null) return;
        _ = ScanFolderAsync(folder);
    }

    private async Task ScanFolderAsync(string folder)
    {
        List<LargeFile> list = new();
        await RunBusyAsync($"Scanning {folder}…", async ct =>
        {
            list = await _storage.FindLargestFilesAsync(folder, 100, n => Busy.Report(-1, $"Scanning… {n:N0} files checked"), ct);
        });
        LargeFiles.Clear();
        foreach (var f in list) LargeFiles.Add(new FileRow(f));
        LargeFilesSummary = list.Count == 0 ? "No files found." : $"{list.Count} largest files in {folder} · {Format.Bytes(list.Sum(f => f.Size))} in total";
    }

    [RelayCommand]
    private void ShowInExplorer(FileRow? f)
    {
        if (f != null) ShellHelper.ShowInExplorer(f.FullPath);
    }

    [RelayCommand]
    private Task RecycleLargeFilesAsync() => RecycleAsync(LargeFiles, "large files");

    // ------------------------------------------------------------------ old downloads
    [RelayCommand]
    private async Task ScanDownloadsAsync()
    {
        List<LargeFile> list = new();
        var days = DownloadAge;
        await RunBusyAsync("Looking for old downloads…", async ct => list = await _storage.FindOldDownloadsAsync(days, ct));
        OldDownloads.Clear();
        foreach (var f in list) OldDownloads.Add(new FileRow(f));
        DownloadsSummary = list.Count == 0
            ? $"No files older than {days} days in Downloads."
            : $"{list.Count} files older than {days} days · {Format.Bytes(list.Sum(f => f.Size))}. Nothing is selected — tick what you want to remove.";
    }

    partial void OnDownloadAgeChanged(int value)
    {
        if (OldDownloads.Count > 0 || !string.IsNullOrEmpty(DownloadsSummary)) _ = ScanDownloadsAsync();
    }

    [RelayCommand]
    private void OpenDownloads()
    {
        if (StorageService.DownloadsFolder is { } d && Directory.Exists(d)) ShellHelper.OpenFolder(d);
    }

    [RelayCommand]
    private Task RecycleDownloadsAsync() => RecycleAsync(OldDownloads, "downloads");

    [RelayCommand]
    private void CheckAll(ObservableCollection<FileRow>? rows)
    {
        if (rows == null) return;
        var target = !rows.All(r => r.IsChecked);
        foreach (var r in rows) r.IsChecked = target;
    }

    /// <summary>Moves the ticked files to the Recycle Bin (never a permanent delete) after confirmation.</summary>
    private async Task RecycleAsync(ObservableCollection<FileRow> rows, string what)
    {
        var picked = rows.Where(r => r.IsChecked).ToList();
        if (picked.Count == 0) { Dialogs.Toast("Tick the files you want to remove first."); return; }
        if (!await Dialogs.ConfirmAsync($"Move {picked.Count} file(s) to the Recycle Bin?",
                $"{Format.Bytes(picked.Sum(p => p.Size))} will be moved to the Recycle Bin. You can still restore them from there. Space is freed when the Recycle Bin is emptied.",
                picked.Take(15).Select(p => $"{p.Name} — {Format.Bytes(p.Size)}").Concat(picked.Count > 15 ? new[] { $"…and {picked.Count - 15} more" } : Array.Empty<string>()),
                "Move to Recycle Bin")) return;

        var failed = new List<string>();
        int moved = 0;
        await RunBusyAsync($"Moving {what} to the Recycle Bin…", ct => Task.Run(() =>
        {
            for (int i = 0; i < picked.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = picked[i];
                Busy.Report(100.0 * i / picked.Count, $"Moving {p.Name}…");
                if (!PathGuard.IsUserRecyclable(p.FullPath, out var reason)) { failed.Add($"{p.Name}: {reason}"); continue; }
                if (ShellHelper.MoveToRecycleBin(p.FullPath, out var err))
                {
                    moved++;
                    Log.Information("Moved to Recycle Bin: {Path}", p.FullPath);
                    System.Windows.Application.Current.Dispatcher.Invoke(() => rows.Remove(p));
                }
                else failed.Add($"{p.Name}: {err ?? "in use or access denied"}");
            }
        }, ct));
        _stats.RefreshDiskNow();
        if (failed.Count == 0) Dialogs.Toast($"Moved {moved} file(s) to the Recycle Bin.", ToastKind.Success);
        else await Dialogs.InfoAsync("Some files were not moved", $"Moved {moved} file(s). {failed.Count} couldn't be moved:", failed);
    }
}
