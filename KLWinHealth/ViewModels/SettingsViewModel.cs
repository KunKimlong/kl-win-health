using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Models;
using KLWinHealth.Services;
using Serilog;

namespace KLWinHealth.ViewModels;

public partial class SettingsViewModel : PageViewModel
{
    private readonly SchedulerService _scheduler;
    private bool _loading;

    public override string Title => "Settings";

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _hasVirusTotalKey;
    [ObservableProperty] private bool _autoCleanEnabled;
    [ObservableProperty] private DayOfWeek _autoCleanDay;
    [ObservableProperty] private string _autoCleanTime = "12:00";
    [ObservableProperty] private string _autoCleanStatus = "";
    [ObservableProperty] private BackupRecord? _selectedBackup;
    [ObservableProperty] private bool _hideRestored = true;

    public ObservableCollection<BackupRecord> BackupRecords { get; } = new();
    public IReadOnlyList<DayOfWeek> Days { get; } = Enum.GetValues<DayOfWeek>();

    public string AppName => "KL Win Health";
    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string DataFolder => AppPaths.Root;
    public bool IsAdmin { get; } = ShellHelper.IsAdministrator();

    // Updates
    private readonly UpdateService _updates;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private UpdateInfo? _availableUpdate;
    public bool UpdatesConfigured => UpdateService.IsConfigured;

    public SettingsViewModel(PageContext ctx, SchedulerService scheduler, UpdateService updates) : base(ctx)
    {
        _scheduler = scheduler;
        _updates = updates;
        _updates.UpdateAvailable += info => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ShowUpdate(info));
        Backups.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(LoadBackups);
    }

    public override async Task OnNavigatedToAsync()
    {
        _loading = true;
        try
        {
            var s = Settings.Current;
            MinimizeToTray = s.MinimizeToTrayOnClose;
            HasVirusTotalKey = !string.IsNullOrEmpty(Settings.GetVirusTotalKey());
            AutoCleanEnabled = s.AutoCleanEnabled;
            AutoCleanDay = s.AutoCleanDay;
            AutoCleanTime = s.AutoCleanTime;
            CheckForUpdates = s.CheckForUpdates;
            if (_updates.Available is { } u) ShowUpdate(u);
            else UpdateStatus = !UpdatesConfigured ? "Updates are not set up for this build."
                : s.LastUpdateCheck is { } t ? $"Up to date · last checked {t.ToLocalTime():g}" : "Not checked yet";
            LoadBackups();
            StartWithWindows = await _scheduler.ExistsAsync(SchedulerService.StartupTask);
            var cleanTask = await _scheduler.ExistsAsync(SchedulerService.AutoCleanTask);
            AutoCleanStatus = cleanTask ? $"Scheduled: every {s.AutoCleanDay} at {s.AutoCleanTime}" : "Not scheduled";
        }
        finally { _loading = false; }
    }

    partial void OnHideRestoredChanged(bool value) => LoadBackups();

    private void LoadBackups()
    {
        BackupRecords.Clear();
        foreach (var r in Backups.LoadAll().Where(r => !HideRestored || !r.IsFullyRestored))
            BackupRecords.Add(r);
    }

    async partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        try
        {
            if (value) await _scheduler.CreateStartupTaskAsync();
            else await _scheduler.DeleteAsync(SchedulerService.StartupTask);
            Settings.Current.StartWithWindows = value;
            Settings.Save();
            Dialogs.Toast(value ? "KL Win Health will start with Windows."
                : "Start with Windows turned off. Desktop widgets won't appear after a restart until you open the app.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start with Windows toggle failed");
            await Dialogs.ErrorAsync("Start with Windows", FriendlyError(ex));
            _loading = true;
            StartWithWindows = !value;
            _loading = false;
        }
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        if (_loading) return;
        Settings.Current.MinimizeToTrayOnClose = value;
        Settings.Save();
    }

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (_loading) return;
        Settings.Current.CheckForUpdates = value;
        Settings.Save();
    }

    private void ShowUpdate(UpdateInfo info)
    {
        AvailableUpdate = info;
        UpdateStatus = $"Version {info.Version.ToString(3)} is available (you have {UpdateService.CurrentVersion.ToString(3)}).";
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        await RunBusyAsync("Checking for updates…", async ct =>
        {
            var info = await _updates.CheckAsync(ct);
            if (info == null)
            {
                AvailableUpdate = null;
                UpdateStatus = $"Up to date · last checked {DateTime.Now:g}";
                Dialogs.Toast($"You have the latest version ({UpdateService.CurrentVersion.ToString(3)}).", ToastKind.Success);
            }
            else ShowUpdate(info);
        }, errorTitle: "Check for updates");
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } info) return;
        if (!await Dialogs.ConfirmAsync("Install update",
                $"Download and install KL Win Health {info.Version.ToString(3)}? The app closes for a few seconds and reopens; widgets come back by themselves.",
                string.IsNullOrWhiteSpace(info.Notes) ? null : info.Notes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(12),
                "Install")) return;
        string? exe = null;
        await RunBusyAsync("Downloading update…", async ct =>
        {
            exe = await _updates.DownloadAsync(info, new Progress<double>(p => Busy.Report(p, $"Downloading update… {p:0}%")), ct);
        }, errorTitle: "Install update");
        if (exe == null) return;
        try
        {
            UpdateService.LaunchInstaller(exe);
            ((App)System.Windows.Application.Current).ExitApp();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Starting the update failed");
            await Dialogs.ErrorAsync("Install update", FriendlyError(ex));
        }
    }

    [RelayCommand] private void OpenReleases() => ShellHelper.OpenUrl(AvailableUpdate?.ReleaseUrl ?? UpdateService.ReleasesPage);

    [RelayCommand]
    private void SaveVirusTotalKey(object? parameter)
    {
        var key = (parameter as System.Windows.Controls.PasswordBox)?.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            Dialogs.Toast("Paste your VirusTotal API key first.", ToastKind.Warning);
            return;
        }
        if (key.Length < 32 || !key.All(Uri.IsHexDigit))
        {
            Dialogs.Toast("That doesn't look like a VirusTotal API key (64 hex characters).", ToastKind.Warning);
            return;
        }
        Settings.SetVirusTotalKey(key);
        ((System.Windows.Controls.PasswordBox)parameter!).Clear();
        HasVirusTotalKey = true;
        Log.Information("VirusTotal API key saved (encrypted with DPAPI)");
        Dialogs.Toast("VirusTotal API key saved (encrypted).", ToastKind.Success);
    }

    [RelayCommand]
    private void RemoveVirusTotalKey()
    {
        Settings.SetVirusTotalKey(null);
        HasVirusTotalKey = false;
        Dialogs.Toast("VirusTotal API key removed.");
    }

    [RelayCommand]
    private async Task ApplyAutoCleanAsync()
    {
        if (!TimeSpan.TryParseExact(AutoCleanTime.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time))
        {
            await Dialogs.ErrorAsync("Auto-clean", "Enter the time as HH:mm, for example 12:30.");
            return;
        }
        await RunBusyAsync("Updating scheduled task…", async _ =>
        {
            var s = Settings.Current;
            if (AutoCleanEnabled) await _scheduler.CreateAutoCleanTaskAsync(AutoCleanDay, time);
            else await _scheduler.DeleteAsync(SchedulerService.AutoCleanTask);
            s.AutoCleanEnabled = AutoCleanEnabled;
            s.AutoCleanDay = AutoCleanDay;
            s.AutoCleanTime = time.ToString(@"hh\:mm");
            AutoCleanTime = s.AutoCleanTime;
            Settings.Save();
            AutoCleanStatus = AutoCleanEnabled ? $"Scheduled: every {s.AutoCleanDay} at {s.AutoCleanTime}" : "Not scheduled";
            Dialogs.Toast(AutoCleanEnabled ? "Weekly auto-clean scheduled (Safe categories only)." : "Weekly auto-clean turned off.", ToastKind.Success);
        }, cancellable: false, errorTitle: "Auto-clean");
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync()
    {
        if (SelectedBackup is not { } r) return;
        if (r.IsFullyRestored)
        {
            Dialogs.Toast("This backup was already restored.");
            return;
        }
        if (!await Dialogs.ConfirmAsync("Restore backup",
                $"Put these items back to how they were on {r.DateText}?", r.Entries.Where(e => !e.Restored).Select(e => e.Display),
                "Restore")) return;
        await RunBusyAsync("Restoring…", async ct =>
        {
            var res = await Backups.RestoreRecordAsync(r, ct);
            await ShowResult(res);
        }, errorTitle: "Restore");
        LoadBackups();
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        var active = Backups.LoadAll().SelectMany(r => r.Entries).Where(e => !e.Restored).ToList();
        if (active.Count == 0)
        {
            await Dialogs.InfoAsync("Restore everything", "There is nothing to restore — no active changes were found.");
            return;
        }
        if (!await Dialogs.ConfirmAsync("Restore everything",
                "Every service, registry/policy value and wallpaper changed by KL Win Health will be put back to its ORIGINAL state.",
                active.GroupBy(e => e.Target).Select(g => g.Last().Display), "Restore everything", danger: true)) return;
        // A running slideshow would immediately replace the restored wallpaper.
        if (App.Services.GetService(typeof(WallpaperService)) is WallpaperService wp && wp.IsSlideshowRunning) wp.StopSlideshow();
        await RunBusyAsync("Restoring everything…", async ct =>
        {
            var res = await Backups.RestoreModuleAsync(null, ct);
            await ShowResult(res);
        }, errorTitle: "Restore everything");
        LoadBackups();
    }

    private Task ShowResult(RestoreResult res) =>
        res.Failed == 0
            ? Dialogs.SuccessAsync("Restore complete", res.Summary)
            : Dialogs.ErrorAsync("Restore finished with errors", $"{res.Restored} restored, {res.Failed} failed.", res.Errors);

    [RelayCommand] private void RefreshBackups() => LoadBackups();
    [RelayCommand] private void OpenLogs() => ShellHelper.OpenFolder(AppPaths.Logs);
    [RelayCommand] private void OpenQuarantine() => ShellHelper.OpenFolder(AppPaths.Quarantine);
    [RelayCommand] private void OpenBackups() => ShellHelper.OpenFolder(AppPaths.Backups);
    [RelayCommand] private void OpenConfig() => ShellHelper.OpenFolder(AppPaths.Config);
    [RelayCommand] private void OpenVirusTotalSite() => ShellHelper.OpenUrl("https://www.virustotal.com/gui/my-apikey");
}
