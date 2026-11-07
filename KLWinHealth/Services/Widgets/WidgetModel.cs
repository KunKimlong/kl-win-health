using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using KLWinHealth.Converters;
using KLWinHealth.Models;

namespace KLWinHealth.Services.Widgets;

public record CalendarDay(string Text, bool IsToday, bool IsOtherMonth, bool IsWeekend);

public partial class MonitorRow : ObservableObject
{
    public string Label { get; init; } = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _text = "";
}

/// <summary>Live view model of one desktop widget: its config (style) plus the data it shows.</summary>
public partial class WidgetModel : ObservableObject
{
    private readonly SystemStatsService _stats;

    public WidgetConfig Config { get; }

    [ObservableProperty] private bool _isEditMode;

    // Style (derived from Config)
    [ObservableProperty] private Brush _textBrush = Brushes.White;
    [ObservableProperty] private Brush _mutedBrush = Brushes.LightGray;
    [ObservableProperty] private Brush _backgroundBrush = Brushes.Transparent;
    [ObservableProperty] private FontFamily _font = new("Segoe UI");
    [ObservableProperty] private bool _hasBackground;

    // Clock
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _amPmText = "";
    [ObservableProperty] private string _dateText = "";

    // Calendar
    [ObservableProperty] private string _monthTitle = "";
    public string[] DayNames { get; }
    public ObservableCollection<CalendarDay> Days { get; } = new();
    private int _calendarFor = -1;

    // Monitor
    public ObservableCollection<MonitorRow> MonitorRows { get; } = new();

    // Network
    [ObservableProperty] private string _downText = "";
    [ObservableProperty] private string _upText = "";

    // Battery
    [ObservableProperty] private string _batteryText = "";
    [ObservableProperty] private double _batteryPercent;
    [ObservableProperty] private string _batteryStatus = "";
    [ObservableProperty] private bool _batteryCharging;
    [ObservableProperty] private bool _batteryLow;

    public WidgetModel(WidgetConfig config, SystemStatsService stats)
    {
        Config = config;
        _stats = stats;
        var c = CultureInfo.CurrentCulture;
        var first = (int)c.DateTimeFormat.FirstDayOfWeek;
        DayNames = Enumerable.Range(0, 7).Select(i => c.DateTimeFormat.AbbreviatedDayNames[(first + i) % 7]).Select(n => n.Length > 2 ? n[..2] : n).ToArray();
        Config.PropertyChanged += OnConfigChanged;
        ApplyStyle();
        RebuildMonitorRows();
        Tick(DateTime.Now);
    }

    public void Detach() => Config.PropertyChanged -= OnConfigChanged;

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WidgetConfig.TextColor):
            case nameof(WidgetConfig.BackgroundColor):
            case nameof(WidgetConfig.BackgroundOpacity):
            case nameof(WidgetConfig.FontFamily):
                ApplyStyle(); break;
            case nameof(WidgetConfig.ShowCpu):
            case nameof(WidgetConfig.ShowRam):
            case nameof(WidgetConfig.ShowGpu):
            case nameof(WidgetConfig.ShowDisk):
                RebuildMonitorRows(); break;
        }
        Tick(DateTime.Now);
    }

    public static Color ParseColor(string? s, Color fallback)
    {
        try { return s != null && ColorConverter.ConvertFromString(s) is Color col ? col : fallback; }
        catch { return fallback; }
    }

    private void ApplyStyle()
    {
        var text = ParseColor(Config.TextColor, Colors.White);
        TextBrush = Freeze(new SolidColorBrush(text));
        MutedBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, text.R, text.G, text.B)));
        var bg = ParseColor(Config.BackgroundColor, Colors.Black);
        var a = (byte)Math.Round(Math.Clamp(Config.BackgroundOpacity, 0, 100) * 2.55);
        // Fully transparent pixels would not receive mouse input in edit mode, so keep a 1/255 alpha minimum.
        BackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(Math.Max(a, (byte)1), bg.R, bg.G, bg.B)));
        HasBackground = a > 0;
        // "Rajdhani" is bundled with the app (not installed), so use the app's font resource for it.
        if (string.Equals(Config.FontFamily, "Rajdhani", StringComparison.OrdinalIgnoreCase) &&
            System.Windows.Application.Current?.TryFindResource("HeadingFont") is FontFamily heading)
        {
            Font = heading;
            return;
        }
        try { Font = new FontFamily(string.IsNullOrWhiteSpace(Config.FontFamily) ? "Segoe UI" : Config.FontFamily); }
        catch { Font = new FontFamily("Segoe UI"); }
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private void RebuildMonitorRows()
    {
        MonitorRows.Clear();
        if (Config.ShowCpu) MonitorRows.Add(new MonitorRow { Label = "CPU" });
        if (Config.ShowRam) MonitorRows.Add(new MonitorRow { Label = "RAM" });
        if (Config.ShowGpu) MonitorRows.Add(new MonitorRow { Label = "GPU" });
        if (Config.ShowDisk) MonitorRows.Add(new MonitorRow { Label = "DISK" });
    }

    /// <summary>Called every second by the widget manager.</summary>
    public void Tick(DateTime now)
    {
        switch (Config.Type)
        {
            case WidgetType.Clock:
                var fmt = Config.Use24Hour ? (Config.ShowSeconds ? "HH:mm:ss" : "HH:mm") : (Config.ShowSeconds ? "h:mm:ss" : "h:mm");
                TimeText = now.ToString(fmt, CultureInfo.CurrentCulture);
                AmPmText = Config.Use24Hour ? "" : now.ToString("tt", CultureInfo.InvariantCulture);
                DateText = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
                break;

            case WidgetType.Calendar:
                var key = now.Year * 10000 + now.Month * 100 + now.Day;
                if (key != _calendarFor) { _calendarFor = key; BuildCalendar(now); }
                break;

            case WidgetType.SystemMonitor:
                foreach (var r in MonitorRows)
                {
                    var v = r.Label switch
                    {
                        "CPU" => _stats.CpuPercent,
                        "RAM" => _stats.RamPercent,
                        "GPU" => _stats.GpuPercent,
                        "DISK" => _stats.DiskActivityPercent,
                        _ => 0
                    };
                    r.Percent = Math.Clamp(v, 0, 100);
                    r.Text = r.Label == "RAM" && _stats.RamTotal > 0
                        ? $"{v:0}%  ·  {Format.Bytes((long)_stats.RamUsed)}"
                        : $"{v:0}%";
                }
                break;

            case WidgetType.NetworkSpeed:
                DownText = Format.Speed(_stats.NetDownBytesPerSec);
                UpText = Format.Speed(_stats.NetUpBytesPerSec);
                break;

            case WidgetType.Battery:
                if (!_stats.HasBattery || _stats.BatteryPercent < 0)
                {
                    BatteryText = "—";
                    BatteryStatus = "No battery";
                    BatteryPercent = 0;
                    BatteryCharging = false;
                    BatteryLow = false;
                }
                else
                {
                    BatteryPercent = _stats.BatteryPercent;
                    BatteryText = $"{_stats.BatteryPercent}%";
                    BatteryCharging = _stats.BatteryCharging;
                    BatteryLow = _stats.BatteryPercent <= 20 && !_stats.OnAcPower;
                    BatteryStatus = _stats.BatteryCharging ? "Charging" : _stats.OnAcPower ? "Plugged in" : "On battery";
                }
                break;
        }
    }

    private void BuildCalendar(DateTime now)
    {
        var c = CultureInfo.CurrentCulture;
        MonthTitle = now.ToString("MMMM yyyy", c);
        Days.Clear();
        var first = new DateTime(now.Year, now.Month, 1);
        var firstDow = (int)c.DateTimeFormat.FirstDayOfWeek;
        var offset = ((int)first.DayOfWeek - firstDow + 7) % 7;
        var start = first.AddDays(-offset);
        for (int i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            if (i == 35 && d.Month != now.Month) break; // drop an empty 6th week
            Days.Add(new CalendarDay(d.Day.ToString(c), d.Date == now.Date, d.Month != now.Month,
                d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday));
        }
    }
}
