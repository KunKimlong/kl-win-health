using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace KLWinHealth.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && (v == Visibility.Visible) != Invert;
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value != null && !(value is string s && string.IsNullOrEmpty(s));
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class ZeroToVisibilityConverter : IValueConverter
{
    /// <summary>false: visible when zero. true: visible when non-zero.</summary>
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var zero = value == null || System.Convert.ToDouble(value, CultureInfo.InvariantCulture) == 0;
        return zero != Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

public class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        long l => Format.Bytes(l),
        ulong u => Format.Bytes((long)u),
        int i => Format.Bytes(i),
        double d => Format.Bytes((long)d),
        _ => ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Maps status words (Clean/Safe/Suspicious/Caution/Dangerous/Critical/...) to the theme colours.</summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        StatusColors.Brush(value?.ToString(), soft: false);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class StatusToSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        StatusColors.Brush(value?.ToString(), soft: true);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class StatusColors
{
    private static readonly SolidColorBrush Green = Frozen("#22E36B"), GreenSoft = Frozen("#1A22E36B");
    private static readonly SolidColorBrush Yellow = Frozen("#F5B700"), YellowSoft = Frozen("#1AF5B700");
    private static readonly SolidColorBrush Red = Frozen("#FF4D4F"), RedSoft = Frozen("#1AFF4D4F");
    private static readonly SolidColorBrush Grey = Frozen("#86A893"), GreySoft = Frozen("#1486A893");

    public static Brush Brush(string? status, bool soft)
    {
        switch (status?.ToLowerInvariant())
        {
            case "clean": case "safe": case "running": case "enabled": case "applied": case "ok": case "protected-ok":
                return soft ? GreenSoft : Green;
            case "suspicious": case "caution": case "warning": case "unknown":
                return soft ? YellowSoft : Yellow;
            case "dangerous": case "critical": case "required": case "protected": case "error": case "threat":
                return soft ? RedSoft : Red;
            default:
                return soft ? GreySoft : Grey;
        }
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>bool → status word (for Badge Tag colouring).</summary>
public class BoolToStatusConverter : IValueConverter
{
    public string TrueValue { get; set; } = "Ok";
    public string FalseValue { get; set; } = "Error";
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? TrueValue : FalseValue;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>values[0] = percent (0–100), values[1] = available width.</summary>
public class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double p || values[1] is not double w || double.IsNaN(w)) return 0.0;
        return Math.Max(0, Math.Min(100, p)) / 100.0 * w;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when value.ToString() equals the parameter (for radio-button enum bindings).</summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var eq = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        // Also usable for Visibility (visible when equal).
        if (targetType == typeof(System.Windows.Visibility)) return eq ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        return eq;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        if (targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!);
        return parameter;
    }
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : v >= 100 ? $"{v:0} {units[u]}" : $"{v:0.#} {units[u]}";
    }

    public static string Speed(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
        return $"{bytesPerSec / 1024 / 1024:0.00} MB/s";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.TotalSeconds:0.0}s";
}

/// <summary>value × parameter (invariant number), e.g. FontSize × 2.5 for a widget's big text.</summary>
public class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = value is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture) : 0;
        var f = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1;
        return v * f;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
