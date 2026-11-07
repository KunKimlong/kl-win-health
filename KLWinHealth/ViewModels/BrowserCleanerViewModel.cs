using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Converters;
using KLWinHealth.Models;
using KLWinHealth.Services;
using Serilog;

namespace KLWinHealth.ViewModels;

/// <summary>Module 4 — Browser cleaner: processes, extensions, memory policies, cache.</summary>
public partial class BrowserCleanerViewModel : PageViewModel
{
    private readonly BrowserService _service;
    private readonly DispatcherTimer _refreshTimer;
    private bool _active;

    public override string Title => "Browser cleaner";
    public override string Subtitle => "See what each browser process does, review extensions, turn on memory saving and clear cache.";

    public ObservableCollection<BrowserInfo> Browsers { get; } = new();
    public ObservableCollection<BrowserProcess> Processes { get; } = new();
    public ObservableCollection<ExtensionInfo> Extensions { get; } = new();
    public ObservableCollection<PolicySetting> Policies { get; } = new();
    public ObservableCollection<CacheEntry> CacheEntries { get; } = new();

    [ObservableProperty] private BrowserInfo? _selectedBrowser;
    [ObservableProperty] private BrowserProcess? _selectedProcess;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _isBrowserRunning;
    [ObservableProperty] private string _processSummary = "";
    [ObservableProperty] private string _extensionSummary = "";
    [ObservableProperty] private string _cacheSummary = "";
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private bool _isManagedNoteVisible;

    public bool NoBrowsers => Browsers.Count == 0;

    public BrowserCleanerViewModel(PageContext ctx, BrowserService service) : base(ctx)
    {
        _service = service;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_active && AutoRefresh && SelectedTab == 0 && !Busy.IsBusy) await RefreshProcessesAsync(quiet: true);
        };
    }

    public override async Task OnNavigatedToAsync()
    {
        _active = true;
        if (Browsers.Count == 0) LoadBrowsers();
        else await RefreshProcessesAsync(quiet: true);
        _refreshTimer.Start();
    }

    public override void OnNavigatedFrom()
    {
        _active = false;
        _refreshTimer.Stop();
    }

    private void LoadBrowsers()
    {
        Browsers.Clear();
        foreach (var b in _service.Detect().Where(b => b.Installed)) Browsers.Add(b);
        OnPropertyChanged(nameof(NoBrowsers));
        Log.Information("Browsers detected: {List}", string.Join(", ", Browsers.Select(b => b.Name)));
        LoadPolicies();
        SelectedBrowser = Browsers.FirstOrDefault(BrowserService.IsRunning) ?? Browsers.FirstOrDefault();
    }

    partial void OnSelectedBrowserChanged(BrowserInfo? value)
    {
        Processes.Clear();
        Extensions.Clear();
        if (value == null) return;
        _ = RefreshProcessesAsync(quiet: false);
        _ = LoadExtensionsAsync();
    }

    partial void OnSelectedTabChanged(int value)
    {
        if (value == 3 && CacheEntries.Count == 0) _ = AnalyzeCacheAsync();
    }

    // ------------------------------------------------------------------ processes
    [RelayCommand]
    private Task RefreshAsync() => SelectedTab switch
    {
        1 => LoadExtensionsAsync(),
        2 => Task.Run(LoadPolicies),
        3 => AnalyzeCacheAsync(),
        _ => RefreshProcessesAsync(quiet: false)
    };

    private bool _refreshing;
    private async Task RefreshProcessesAsync(bool quiet)
    {
        var b = SelectedBrowser;
        if (b == null || _refreshing) return;
        _refreshing = true;
        try
        {
            var list = await _service.GetProcessesAsync(b, CancellationToken.None);
            if (b != SelectedBrowser) return;
            var selPid = SelectedProcess?.Pid;
            // Update in place so the selection and scroll position survive the auto-refresh.
            var byPid = list.ToDictionary(p => p.Pid);
            for (int i = Processes.Count - 1; i >= 0; i--)
            {
                if (byPid.TryGetValue(Processes[i].Pid, out var fresh))
                {
                    Processes[i].WorkingSet = fresh.WorkingSet;
                    Processes[i].PrivateBytes = fresh.PrivateBytes;
                    Processes[i].CpuPercent = fresh.CpuPercent;
                    byPid.Remove(fresh.Pid);
                }
                else Processes.RemoveAt(i);
            }
            foreach (var p in list.Where(p => byPid.ContainsKey(p.Pid))) Processes.Add(p);
            if (selPid != null) SelectedProcess = Processes.FirstOrDefault(p => p.Pid == selPid);

            IsBrowserRunning = Processes.Count > 0;
            var total = Processes.Sum(p => p.PrivateBytes);
            ProcessSummary = IsBrowserRunning
                ? $"{Processes.Count} processes · {Format.Bytes(total)} memory · {Processes.Count(p => p.CanEnd)} safe to end"
                : $"{b.Name} is not running.";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Listing {Browser} processes failed", b.Name);
            if (!quiet) Dialogs.Toast("Couldn't list browser processes: " + FriendlyError(ex), ToastKind.Warning);
        }
        finally { _refreshing = false; }
    }

    [RelayCommand]
    private async Task EndProcessAsync(BrowserProcess? p)
    {
        if (p == null) return;
        if (!p.CanEnd)
        {
            await Dialogs.InfoAsync("Not recommended",
                $"\"{p.Label}\" is marked {p.SafetyText}. Ending it can crash tabs or the whole browser, so KL Win Health doesn't do it.\n\n" +
                "Tip: press Shift+Esc in Chrome, Edge or Brave to open the browser's own Task Manager and close a single tab or extension.");
            return;
        }
        if (!await Dialogs.ConfirmAsync("End process?",
                $"End \"{p.Label}\" (PID {p.Pid}, {Format.Bytes(p.PrivateBytes)})?",
                new[] { p.Detail }, "End process")) return;
        try
        {
            BrowserService.EndProcess(p.Pid);
            Dialogs.Toast($"Ended {p.Label} — freed about {Format.Bytes(p.PrivateBytes)}.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "End process {Pid} failed", p.Pid);
            Dialogs.Toast("Couldn't end the process: " + FriendlyError(ex), ToastKind.Warning);
        }
        await RefreshProcessesAsync(quiet: true);
    }

    [RelayCommand]
    private async Task CloseBrowserAsync()
    {
        var b = SelectedBrowser;
        if (b == null || !BrowserService.IsRunning(b)) return;
        if (!await Dialogs.ConfirmAsync($"Close {b.Name}?",
                $"All {b.Name} windows will be asked to close. Save any work in open tabs first.", null, "Close browser")) return;
        await CloseAsync(b);
        await RefreshProcessesAsync(quiet: true);
    }

    /// <summary>Polite close, then (after confirmation) force close. Returns true if the browser is no longer running.</summary>
    private async Task<bool> CloseAsync(BrowserInfo b)
    {
        bool closed = false;
        await RunBusyAsync($"Closing {b.Name}…", async ct => closed = await _service.CloseBrowserAsync(b, false, ct));
        if (closed) return true;
        if (!await Dialogs.ConfirmAsync($"{b.Name} is still running",
                "It didn't close within 10 seconds (maybe a \"Leave site?\" prompt is open, or it keeps running in the background).\n\nForce it to close? Unsaved form data in open tabs will be lost; tabs can be restored on next start.",
                null, "Force close", danger: true)) return false;
        await RunBusyAsync($"Force-closing {b.Name}…", async ct => closed = await _service.CloseBrowserAsync(b, true, ct), cancellable: false);
        return closed;
    }

    [RelayCommand]
    private void OpenBrowserTaskManagerTip() =>
        Dialogs.InfoAsync("Browser Task Manager",
            "Chrome, Edge and Brave have their own Task Manager that shows each tab and extension by name.\n\n" +
            "Open the browser and press Shift+Esc. Select a tab or extension and click \"End process\".\n\n" +
            "Firefox: type about:processes in the address bar.");

    // ------------------------------------------------------------------ extensions
    private async Task LoadExtensionsAsync()
    {
        var b = SelectedBrowser;
        if (b == null) return;
        try
        {
            var list = await _service.GetExtensionsAsync(b, CancellationToken.None);
            if (b != SelectedBrowser) return;
            Extensions.Clear();
            foreach (var e in list) Extensions.Add(e);
            ExtensionSummary = list.Count == 0
                ? "No extensions installed."
                : $"{list.Count} extensions · {list.Count(e => e.Enabled == true)} enabled · {Format.Bytes(list.Sum(e => e.Size))} on disk";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Reading {Browser} extensions failed", b.Name);
            ExtensionSummary = "Couldn't read extensions: " + FriendlyError(ex);
        }
    }

    [RelayCommand]
    private void OpenExtensionsPage()
    {
        var b = SelectedBrowser;
        if (b?.ExePath == null) { Dialogs.Toast("Browser program not found.", ToastKind.Warning); return; }
        try
        {
            // Started as the normal user (not elevated) so it opens in the user's real browser session.
            ShellHelper.LaunchUnelevated(b.ExePath, b.ExtensionsUrl);
            Dialogs.Toast($"Opened {b.ExtensionsUrl} — remove or turn off extensions you don't use.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Opening extensions page failed");
            Dialogs.Toast("Couldn't open the browser: " + FriendlyError(ex), ToastKind.Warning);
        }
    }

    [RelayCommand]
    private void OpenExtensionFolder(ExtensionInfo? e)
    {
        if (e == null || string.IsNullOrEmpty(e.Path)) return;
        if (System.IO.Directory.Exists(e.Path)) ShellHelper.OpenFolder(e.Path);
        else if (System.IO.File.Exists(e.Path)) ShellHelper.ShowInExplorer(e.Path);
    }

    // ------------------------------------------------------------------ policies
    private void LoadPolicies()
    {
        var list = _service.GetPolicies(Browsers);
        void Apply()
        {
            Policies.Clear();
            foreach (var p in list) Policies.Add(p);
            IsManagedNoteVisible = list.Any(p => p.IsApplied);
        }
        var d = System.Windows.Application.Current.Dispatcher;
        if (d.CheckAccess()) Apply(); else d.Invoke(Apply);
    }

    [RelayCommand]
    private async Task ApplyPolicyAsync(PolicySetting? p)
    {
        if (p == null || p.IsApplied) return;
        if (!App.IsAdmin) { Dialogs.Toast("Browser policies need administrator rights.", ToastKind.Warning); return; }
        if (!await Dialogs.ConfirmAsync("Apply setting?",
                $"{p.BrowserName}: {p.Name}",
                new[] { $"HKLM\\{p.KeyPath}\\{p.ValueName} = {p.OptimizedValue}", "The old value is backed up first — use Undo or Restore to go back." },
                "Apply",
                warning: $"{p.BrowserName} will show \"Managed by your organization\" while a policy is set. This is normal and goes away when you restore.")) return;
        await RunBusyAsync("Applying…", _ => Task.Run(() => _service.ApplyPolicy(p)), cancellable: false);
        IsManagedNoteVisible = Policies.Any(x => x.IsApplied);
        if (p.IsApplied) Dialogs.Toast($"Applied. Restart {p.BrowserName} for it to take effect.", ToastKind.Success);
    }

    [RelayCommand]
    private async Task ApplyAllPoliciesAsync()
    {
        var todo = Policies.Where(p => !p.IsApplied).ToList();
        if (todo.Count == 0) { Dialogs.Toast("All recommended settings are already applied.", ToastKind.Success); return; }
        if (!App.IsAdmin) { Dialogs.Toast("Browser policies need administrator rights.", ToastKind.Warning); return; }
        if (!await Dialogs.ConfirmAsync("Apply recommended settings?",
                "These browser policies will be set (old values are backed up first):",
                todo.Select(p => $"{p.BrowserName}: {p.Name}"), "Apply all",
                warning: "Browsers show \"Managed by your organization\" while a policy is set. This is normal and goes away when you restore.")) return;
        await RunBusyAsync("Applying browser settings…", _ => Task.Run(() =>
        {
            foreach (var p in todo) _service.ApplyPolicy(p);
        }), cancellable: false);
        IsManagedNoteVisible = Policies.Any(x => x.IsApplied);
        Dialogs.Toast($"Applied {todo.Count(p => p.IsApplied)} setting(s). Restart the browsers for them to take effect.", ToastKind.Success);
    }

    [RelayCommand]
    private async Task UndoPolicyAsync(PolicySetting? p)
    {
        if (p == null) return;
        bool ok = false;
        await RunBusyAsync("Restoring…", async ct => ok = await _service.UndoPolicyAsync(p, ct), cancellable: false);
        IsManagedNoteVisible = Policies.Any(x => x.IsApplied);
        Dialogs.Toast(ok ? "Restored the original value." : "No backup found for this setting.", ok ? ToastKind.Success : ToastKind.Warning);
    }

    [RelayCommand]
    private async Task RestoreModuleAsync()
    {
        if (!Backups.HasActiveBackups(Modules.Browser)) { Dialogs.Toast("Nothing to restore — no browser settings were changed."); return; }
        if (!await Dialogs.ConfirmAsync("Restore browser settings?", "Every browser policy changed by KL Win Health goes back to its original value.", null, "Restore")) return;
        RestoreResult? r = null;
        await RunBusyAsync("Restoring browser settings…", async ct => r = await Backups.RestoreModuleAsync(Modules.Browser, ct), cancellable: false);
        foreach (var p in Policies) _service.RefreshPolicy(p);
        IsManagedNoteVisible = Policies.Any(x => x.IsApplied);
        if (r != null)
        {
            if (r.Failed == 0) await Dialogs.SuccessAsync("Restored", r.Summary);
            else await Dialogs.ErrorAsync("Restore finished with errors", r.Summary);
        }
    }

    // ------------------------------------------------------------------ cache
    [RelayCommand]
    private async Task AnalyzeCacheAsync()
    {
        var entries = _service.GetCacheEntries(Browsers);
        CacheEntries.Clear();
        foreach (var e in entries) CacheEntries.Add(e);
        await RunBusyAsync("Measuring browser cache…", ct => Task.Run(() =>
        {
            int i = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                Busy.Report(100.0 * i++ / Math.Max(1, entries.Count), $"Measuring {e.BrowserName} — {e.Profile}…");
                BrowserService.MeasureCache(e);
            }
        }, ct));
        UpdateCacheSummary();
    }

    private void UpdateCacheSummary()
    {
        var total = CacheEntries.Sum(e => e.TotalSize);
        CacheSummary = CacheEntries.Count == 0 ? "No browser profiles found." : $"{Format.Bytes(total)} of cache in {CacheEntries.Count} profile(s)";
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var selected = CacheEntries.Where(e => e.IsChecked && e.TotalSize > 0).ToList();
        if (selected.Count == 0) { Dialogs.Toast("Select at least one profile with cache to clear."); return; }

        // The browser must be closed, otherwise most cache files are locked.
        var running = Browsers.Where(b => selected.Any(e => e.Browser == b.Id) && BrowserService.IsRunning(b)).ToList();
        if (running.Count > 0)
        {
            if (!await Dialogs.ConfirmAsync("Close browsers first?",
                    "These browsers are running and must be closed to clear their cache:",
                    running.Select(b => b.Name), "Close and continue")) return;
            foreach (var b in running)
                if (!await CloseAsync(b)) { Dialogs.Toast($"{b.Name} is still running — cache not cleared.", ToastKind.Warning); return; }
        }

        if (!await Dialogs.ConfirmAsync("Clear browser cache?",
                $"Delete {Format.Bytes(selected.Sum(e => e.TotalSize))} of cached files?",
                selected.Select(e => $"{e.BrowserName} — {e.Profile}: {Format.Bytes(e.TotalSize)}")
                        .Append("Cookies, passwords, history and bookmarks are NOT touched."),
                "Clear cache")) return;

        long freed = 0; int skipped = 0;
        var ok = await RunBusyAsync("Clearing cache…", ct => Task.Run(() =>
        {
            int i = 0;
            foreach (var e in selected)
            {
                Busy.Report(100.0 * i++ / selected.Count, $"Clearing {e.BrowserName} — {e.Profile}…");
                var r = BrowserService.ClearCache(e, ct);
                freed += r.BytesFreed; skipped += r.FilesSkipped;
                BrowserService.MeasureCache(e);
            }
        }, ct));
        UpdateCacheSummary();
        if (ok)
            await Dialogs.SuccessAsync("Cache cleared", $"Freed {Format.Bytes(freed)}.",
                skipped > 0 ? new[] { $"{skipped} file(s) were in use and skipped." } : null);
        await RefreshProcessesAsync(quiet: true);
    }
}
