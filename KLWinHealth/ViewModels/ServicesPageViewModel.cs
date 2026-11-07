using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Converters;
using KLWinHealth.Models;
using KLWinHealth.Services;
using Serilog;

namespace KLWinHealth.ViewModels;

/// <summary>Shared logic for "Clean ROG service" and "Clean Windows service".</summary>
public abstract partial class ServicesPageViewModel : PageViewModel
{
    protected readonly ServiceManager Manager;
    protected readonly ConfigService Config;
    private readonly ICollectionView _view;

    public ObservableCollection<ServiceItem> Items { get; } = new();
    public ICollectionView ItemsView => _view;

    [ObservableProperty] private ServiceItem? _selected;
    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _ramFreedText = "";
    [ObservableProperty] private int _safeCount;
    [ObservableProperty] private int _cautionCount;
    [ObservableProperty] private int _protectedCount;
    [ObservableProperty] private int _otherCount;
    [ObservableProperty] private int _runningCount;
    [ObservableProperty] private bool _hasActiveBackups;
    [ObservableProperty] private bool _loaded;

    protected abstract string Module { get; }
    /// <summary>Label used for protected items: "Required" (ROG) or "Critical" (Windows).</summary>
    public abstract string ProtectedLabel { get; }
    /// <summary>Label for unclassified services.</summary>
    public abstract string OtherLabel { get; }
    public abstract MahApps.Metro.IconPacks.PackIconMaterialKind PageIcon { get; }

    protected ServicesPageViewModel(PageContext ctx, ServiceManager manager, ConfigService config) : base(ctx)
    {
        Manager = manager;
        Config = config;
        _view = CollectionViewSource.GetDefaultView(Items);
        _view.Filter = o => o is ServiceItem s && Matches(s);
        _view.SortDescriptions.Add(new SortDescription(nameof(ServiceItem.DisplayName), ListSortDirection.Ascending));
    }

    protected abstract Task<List<ServiceItem>> LoadItemsAsync(CancellationToken ct);

    public override async Task OnNavigatedToAsync()
    {
        if (!Loaded) await RefreshAsync();
    }

    private bool Matches(ServiceItem s)
    {
        var ok = Filter switch
        {
            "Safe" => s.Category == RuleCategory.Safe,
            "Caution" => s.Category == RuleCategory.Caution,
            "Protected" => s.IsProtected,
            "Other" => s.Category == RuleCategory.Unknown && !s.IsProtected,
            "Running" => s.IsRunning,
            _ => true
        };
        if (!ok) return false;
        if (string.IsNullOrWhiteSpace(Search)) return true;
        return s.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               s.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               s.Description.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
               s.RuleLabel.Contains(Search, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnFilterChanged(string value) => _view.Refresh();
    partial void OnSearchChanged(string value) => _view.Refresh();

    [RelayCommand]
    protected async Task RefreshAsync()
    {
        var selectedName = Selected?.Name;
        await RunBusyAsync("Reading services…", async ct =>
        {
            var items = await LoadItemsAsync(ct);
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            Selected = selectedName == null ? null : Items.FirstOrDefault(i => i.Name == selectedName);
            UpdateCounts();
            Loaded = true;
        }, errorTitle: "Services");
    }

    private void UpdateCounts()
    {
        SafeCount = Items.Count(i => i.Category == RuleCategory.Safe);
        CautionCount = Items.Count(i => i.Category == RuleCategory.Caution);
        ProtectedCount = Items.Count(i => i.IsProtected);
        OtherCount = Items.Count(i => i.Category == RuleCategory.Unknown && !i.IsProtected);
        RunningCount = Items.Count(i => i.IsRunning);
        HasActiveBackups = Backups.HasActiveBackups(Module);
    }

    private string? WarningFor(ServiceItem s) => s.Category switch
    {
        RuleCategory.Caution => "Caution: " + s.Reason,
        RuleCategory.Unknown => $"{s.DisplayName} is not on the known-safe list ({OtherLabel}). Only continue if you know what it does.",
        _ => null
    };

    // ------------------------------------------------------------------ per-service actions
    [RelayCommand]
    private async Task StopAsync(ServiceItem? item)
    {
        item ??= Selected;
        if (item is null || !item.CanModify) return;
        if (!item.IsRunning) { Dialogs.Toast($"{item.DisplayName} is not running."); return; }
        if (!await Dialogs.ConfirmAsync("Stop service",
                $"Stop {item.DisplayName}? It stays stopped until the next restart (or until something starts it). A backup is saved first.",
                new[] { $"{item.DisplayName} ({item.Name})", $"RAM now: {item.RamText}" }, "Stop", danger: item.Category != RuleCategory.Safe,
                warning: WarningFor(item))) return;
        var ram = item.RamBytes;
        if (await RunBusyAsync($"Stopping {item.DisplayName}…", ct => Manager.StopAsync(Module, item, ct), errorTitle: "Stop service"))
        {
            if (ram > 0) RamFreedText = $"RAM freed: ~{Format.Bytes(ram)}";
            Dialogs.Toast($"{item.DisplayName} stopped.", ToastKind.Success);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DisableAsync(ServiceItem? item)
    {
        item ??= Selected;
        if (item is null || !item.CanModify) return;
        if (!await Dialogs.ConfirmAsync("Disable service",
                $"Set {item.DisplayName} to Disabled and stop it? It won't start again until you restore it. A backup is saved first.",
                new[] { $"{item.DisplayName} ({item.Name})", $"Startup: {item.StartupText} → Disabled" }, "Disable", danger: true,
                warning: WarningFor(item))) return;
        var ram = item.RamBytes;
        if (await RunBusyAsync($"Disabling {item.DisplayName}…", ct => Manager.SetStartupAsync(Module, item, StartupType.Disabled, true, ct), errorTitle: "Disable service"))
        {
            if (ram > 0) RamFreedText = $"RAM freed: ~{Format.Bytes(ram)}";
            Dialogs.Toast($"{item.DisplayName} disabled.", ToastKind.Success);
        }
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ManualAsync(ServiceItem? item)
    {
        item ??= Selected;
        if (item is null || !item.CanModify) return;
        if (item.StartupType == StartupType.Manual) { Dialogs.Toast($"{item.DisplayName} is already Manual."); return; }
        if (!await Dialogs.ConfirmAsync("Set to Manual",
                $"Set {item.DisplayName} to Manual? Windows or apps can still start it when needed. A backup is saved first.",
                new[] { $"Startup: {item.StartupText} → Manual" }, "Set to Manual", warning: WarningFor(item))) return;
        if (await RunBusyAsync($"Updating {item.DisplayName}…", ct => Manager.SetStartupAsync(Module, item, StartupType.Manual, false, ct), errorTitle: "Set to Manual"))
            Dialogs.Toast($"{item.DisplayName} set to Manual.", ToastKind.Success);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RestoreItemAsync(ServiceItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        var original = Backups.FindOriginalService(item.Name);
        if (original == null) { Dialogs.Toast("No backup for this service — nothing to restore."); return; }
        if (!await Dialogs.ConfirmAsync("Restore service", $"Put {item.DisplayName} back to how it was?",
                new[] { original.Display }, "Restore")) return;
        await RunBusyAsync($"Restoring {item.DisplayName}…", async ct =>
        {
            var r = await Backups.RestoreServiceAsync(item.Name, ct);
            if (r.Failed > 0) await Dialogs.ErrorAsync("Restore", r.Summary);
            else Dialogs.Toast($"{item.DisplayName} restored.", ToastKind.Success);
        }, errorTitle: "Restore");
        await RefreshAsync();
    }

    // ------------------------------------------------------------------ module actions
    [RelayCommand]
    private async Task CleanRecommendedAsync()
    {
        if (!Loaded) await RefreshAsync();
        var targets = Items.Where(i => i.Category == RuleCategory.Safe && i.CanModify &&
                                       (i.IsRunning || i.StartupType != StartupType.Disabled)).ToList();
        if (targets.Count == 0)
        {
            await Dialogs.InfoAsync("Clean recommended", "Nothing to do — all services marked Safe are already stopped and disabled.");
            return;
        }
        var estimate = targets.Where(t => t.RamBytes > 0).Sum(t => t.RamBytes);
        if (!await Dialogs.ConfirmAsync("Clean recommended",
                $"These {targets.Count} service(s) are safe to stop. They will be stopped and set to Disabled. " +
                $"{ProtectedLabel} and Caution services are not touched. A backup is saved first — use Restore to undo.",
                targets.Select(t => $"Stop + Disable: {t.DisplayName}  ({t.StartupText}, {(t.IsRunning ? "running" : "stopped")}, RAM {t.RamText})"),
                "Stop + disable", danger: true)) return;

        List<string> errors = new();
        var ok = await RunBusyAsync("Cleaning recommended services…", async ct =>
        {
            errors = await Manager.StopAndDisableAsync(Module, targets, (p, s) => Busy.Report(p, s), ct);
        }, errorTitle: "Clean recommended");
        await RefreshAsync();

        if (!ok) return;
        // RAM measured before the clean, counted only for services that are now actually stopped.
        var freed = targets.Where(t => !Items.Any(i => i.Name == t.Name && i.IsRunning)).Sum(t => Math.Max(0, t.RamBytes));
        RamFreedText = freed > 0 ? $"RAM freed: ~{Format.Bytes(freed)}" : $"Estimated RAM: ~{Format.Bytes(estimate)}";
        Log.Information("[{Module}] Clean recommended: {Count} services, ~{Freed} bytes freed, {Errors} errors", Module, targets.Count, freed, errors.Count);
        if (errors.Count == 0)
            await Dialogs.SuccessAsync("Clean recommended", $"{targets.Count} service(s) stopped and disabled.\nRAM freed: ~{Format.Bytes(freed)} (estimate, own-process services only).");
        else
            await Dialogs.ErrorAsync("Clean recommended", $"{targets.Count - errors.Count} done, {errors.Count} could not be changed:", errors);
    }

    [RelayCommand]
    private async Task RestoreModuleAsync()
    {
        var pending = Backups.LoadAll().Where(r => r.Module == Module).SelectMany(r => r.Entries).Where(e => !e.Restored).ToList();
        if (pending.Count == 0) { await Dialogs.InfoAsync("Restore", "Nothing to restore — this page hasn't changed anything (or it was already restored)."); return; }
        if (!await Dialogs.ConfirmAsync("Restore all", "Put every service changed on this page back to its original state?",
                pending.GroupBy(e => e.Target).Select(g => g.Last().Display), "Restore all")) return;
        await RunBusyAsync("Restoring services…", async ct =>
        {
            var r = await Backups.RestoreModuleAsync(Module, ct);
            if (r.Failed > 0) await Dialogs.ErrorAsync("Restore", $"{r.Restored} restored, {r.Failed} failed.", r.Errors);
            else await Dialogs.SuccessAsync("Restore", r.Summary);
        }, errorTitle: "Restore");
        RamFreedText = "";
        await RefreshAsync();
    }
}
