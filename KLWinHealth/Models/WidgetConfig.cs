using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KLWinHealth.Models;

public enum WidgetType { Text, Clock, Calendar, SystemMonitor, NetworkSpeed, Battery }
public enum CornerStyle { Sharp, Angled, Rounded }
public enum MonitorDisplay { Bars, Numbers }

/// <summary>
/// One desktop widget: type, position (physical screen pixels, virtual-desktop coordinates), scale and style.
/// Observable so the module page's editor and the live widget window update together.
/// </summary>
public partial class WidgetConfig : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private WidgetType _type;
    [ObservableProperty] private int _x = 200;
    [ObservableProperty] private int _y = 200;
    [ObservableProperty] private double _scale = 1.0;

    // Common style
    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private double _fontSize = 16;
    [ObservableProperty] private string _textColor = "#ECFDF5";
    [ObservableProperty] private string _backgroundColor = "#0B0D0C";
    [ObservableProperty] private int _backgroundOpacity = 60;
    [ObservableProperty] private CornerStyle _corner = CornerStyle.Angled;
    [ObservableProperty] private bool _textShadow = true;

    // Text label
    [ObservableProperty] private string _text = "Hello, desktop!";

    // Clock
    [ObservableProperty] private bool _use24Hour = true;
    [ObservableProperty] private bool _showSeconds = true;
    [ObservableProperty] private bool _showDate = true;

    // System monitor
    [ObservableProperty] private bool _showCpu = true;
    [ObservableProperty] private bool _showRam = true;
    [ObservableProperty] private bool _showGpu = true;
    [ObservableProperty] private bool _showDisk = true;
    [ObservableProperty] private MonitorDisplay _monitorDisplay = MonitorDisplay.Bars;

    [JsonIgnore]
    public string DisplayName => Type switch
    {
        WidgetType.Text => "Text label",
        WidgetType.Clock => "Clock",
        WidgetType.Calendar => "Calendar",
        WidgetType.SystemMonitor => "System monitor",
        WidgetType.NetworkSpeed => "Network speed",
        WidgetType.Battery => "Battery",
        _ => Type.ToString()
    };

    [JsonIgnore]
    public string Summary => Type == WidgetType.Text
        ? (Text.Length > 30 ? Text[..30].Replace('\n', ' ') + "…" : Text.Replace('\n', ' '))
        : $"at {X}, {Y} · {Scale:0.##}×";

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(Summary));
    partial void OnXChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnYChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnScaleChanged(double value) => OnPropertyChanged(nameof(Summary));

    public WidgetConfig Clone()
    {
        // JSON round-trip: MemberwiseClone would also copy PropertyChanged subscribers.
        var c = System.Text.Json.JsonSerializer.Deserialize<WidgetConfig>(System.Text.Json.JsonSerializer.Serialize(this))!;
        c.Id = Guid.NewGuid().ToString("N");
        return c;
    }
}
