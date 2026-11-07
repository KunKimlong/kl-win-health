using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Models;
using KLWinHealth.Services;
using KLWinHealth.Services.Widgets;

namespace KLWinHealth.ViewModels;

public record WidgetTypeOption(WidgetType Type, string Name, MahApps.Metro.IconPacks.PackIconMaterialKind Icon, string Description);

/// <summary>Module 7 — Desktop widgets: add, edit style, edit / lock mode.</summary>
public partial class WidgetsViewModel : PageViewModel
{
    private readonly WidgetManager _manager;

    public override string Title => "Desktop widgets";
    public override string Subtitle => "Widgets live on the desktop behind your windows. Edit mode: drag to move, corners to resize. Locked: click-through.";

    public ObservableCollection<WidgetConfig> Widgets => _manager.Widgets;

    public WidgetTypeOption[] Types { get; } =
    {
        new(WidgetType.Clock, "Clock", MahApps.Metro.IconPacks.PackIconMaterialKind.ClockOutline, "Time and date"),
        new(WidgetType.Calendar, "Calendar", MahApps.Metro.IconPacks.PackIconMaterialKind.CalendarMonthOutline, "This month, today highlighted"),
        new(WidgetType.SystemMonitor, "Monitor", MahApps.Metro.IconPacks.PackIconMaterialKind.Speedometer, "System monitor: CPU, RAM, GPU, disk"),
        new(WidgetType.NetworkSpeed, "Network", MahApps.Metro.IconPacks.PackIconMaterialKind.SwapVertical, "Network speed: download / upload"),
        new(WidgetType.Battery, "Battery", MahApps.Metro.IconPacks.PackIconMaterialKind.BatteryHigh, "Charge and status"),
        new(WidgetType.Text, "Text label", MahApps.Metro.IconPacks.PackIconMaterialKind.FormatText, "Any text you like")
    };

    public CornerStyle[] CornerStyles { get; } = Enum.GetValues<CornerStyle>();
    public MonitorDisplay[] MonitorDisplays { get; } = Enum.GetValues<MonitorDisplay>();
    public List<string> Fonts { get; }
    public string[] Swatches { get; } =
    {
        "#ECFDF5", "#FFFFFF", "#22E36B", "#00C2FF", "#F5B700", "#FF4D4F", "#C084FC", "#9CA3AF", "#0B0D0C", "#000000"
    };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private WidgetConfig? _selected;
    [ObservableProperty] private WidgetModel? _selectedModel;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private bool _snapToGrid;
    [ObservableProperty] private int _gridSize;

    public bool HasSelection => Selected != null;

    public WidgetsViewModel(PageContext ctx, WidgetManager manager) : base(ctx)
    {
        _manager = manager;
        _isLocked = manager.IsLocked;
        _snapToGrid = manager.SnapToGrid;
        _gridSize = manager.GridSize;
        _manager.LockedChanged += locked => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _isLocked = locked;
            OnPropertyChanged(nameof(IsLocked));
        });
        Fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source)
            .Append("Rajdhani")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override Task OnNavigatedToAsync()
    {
        if (Selected == null || !Widgets.Contains(Selected)) Selected = Widgets.FirstOrDefault();
        return Task.CompletedTask;
    }

    partial void OnSelectedChanged(WidgetConfig? value) => SelectedModel = value == null ? null : _manager.GetModel(value);

    partial void OnIsLockedChanged(bool value) => _manager.SetLocked(value);
    partial void OnSnapToGridChanged(bool value) => _manager.SetSnap(value, GridSize);
    partial void OnGridSizeChanged(int value) => _manager.SetSnap(SnapToGrid, value);

    /// <summary>Selects a widget (used by "Edit in KL Win Health" on a widget's menu).</summary>
    public void Select(WidgetConfig c) => Selected = c;

    [RelayCommand]
    private void Add(WidgetTypeOption? t)
    {
        if (t == null) return;
        var c = _manager.Add(t.Type);
        Selected = c;
        if (IsLocked)
        {
            IsLocked = false; // edit mode, so the new widget can be dragged into place
            Dialogs.Toast($"{t.Name} added. Drag it where you want it, then click Lock.", ToastKind.Success);
        }
        else Dialogs.Toast($"{t.Name} added — drag it into place on the desktop.", ToastKind.Success);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected == null) return;
        Selected = _manager.Duplicate(Selected);
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected == null) return;
        if (!await Dialogs.ConfirmAsync("Remove widget?", $"Remove the {Selected.DisplayName} widget from the desktop?", null, "Remove", danger: true)) return;
        var idx = Widgets.IndexOf(Selected);
        _manager.Remove(Selected);
        Selected = Widgets.Count == 0 ? null : Widgets[Math.Clamp(idx, 0, Widgets.Count - 1)];
    }

    [RelayCommand]
    private void ToggleLock() => IsLocked = !IsLocked;

    [RelayCommand]
    private void Reattach()
    {
        _manager.ReattachAll();
        Dialogs.Toast($"Widgets re-attached to the desktop ({_manager.Desktop.Layout} layout).");
    }

    [RelayCommand]
    private void SetTextColor(string? hex)
    {
        if (Selected != null && hex != null) Selected.TextColor = hex;
    }

    [RelayCommand]
    private void SetBackgroundColor(string? hex)
    {
        if (Selected != null && hex != null) Selected.BackgroundColor = hex;
    }

    [RelayCommand]
    private void ResetScale()
    {
        if (Selected != null) Selected.Scale = 1.0;
    }

    /// <summary>For the swatch buttons.</summary>
    public static Brush ToBrush(string hex) => new SolidColorBrush(WidgetModel.ParseColor(hex, Colors.Transparent));
}
