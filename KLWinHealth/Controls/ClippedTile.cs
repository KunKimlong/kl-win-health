using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MahApps.Metro.IconPacks;

namespace KLWinHealth.Controls;

/// <summary>
/// Big angled-corner home tile (icon + label). Visual template, hover lift/glow (150ms) and press scale (0.98)
/// live in Themes/RogGreen.xaml (style "ClippedTile").
/// </summary>
public class ClippedTile : Button
{

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(PackIconMaterialKind), typeof(ClippedTile), new PropertyMetadata(PackIconMaterialKind.Apps));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ClippedTile), new PropertyMetadata(""));

    public static readonly DependencyProperty SubLabelProperty = DependencyProperty.Register(
        nameof(SubLabel), typeof(string), typeof(ClippedTile), new PropertyMetadata(null));

    public static readonly DependencyProperty CutSizeProperty = DependencyProperty.Register(
        nameof(CutSize), typeof(double), typeof(ClippedTile), new PropertyMetadata(12.0));

    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.Register(
        nameof(IconBrush), typeof(Brush), typeof(ClippedTile), new PropertyMetadata(null));

    public PackIconMaterialKind Icon { get => (PackIconMaterialKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string? SubLabel { get => (string?)GetValue(SubLabelProperty); set => SetValue(SubLabelProperty, value); }
    public double CutSize { get => (double)GetValue(CutSizeProperty); set => SetValue(CutSizeProperty, value); }
    public Brush? IconBrush { get => (Brush?)GetValue(IconBrushProperty); set => SetValue(IconBrushProperty, value); }
}

/// <summary>Live statistic card (title, big value, optional bar) used on Home.</summary>
public class StatCard : Control
{

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(StatCard), new PropertyMetadata(""));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatCard), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(StatCard), new PropertyMetadata(null));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(PackIconMaterialKind), typeof(StatCard), new PropertyMetadata(PackIconMaterialKind.Chip));
    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(nameof(ValueBrush), typeof(Brush), typeof(StatCard), new PropertyMetadata(null));
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(double), typeof(StatCard), new PropertyMetadata(-1.0));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public PackIconMaterialKind Icon { get => (PackIconMaterialKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Brush? ValueBrush { get => (Brush?)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }
    /// <summary>0–100 shows a thin bar; negative hides it.</summary>
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
}

public enum GlowButtonVariant { Primary, Secondary, Danger, Ghost }

/// <summary>Angled-corner button with optional icon and a green glow on hover.</summary>
public class GlowButton : Button
{

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(PackIconMaterialKind?), typeof(GlowButton), new PropertyMetadata(null));

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant), typeof(GlowButtonVariant), typeof(GlowButton), new PropertyMetadata(GlowButtonVariant.Secondary));

    public PackIconMaterialKind? Icon { get => (PackIconMaterialKind?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public GlowButtonVariant Variant { get => (GlowButtonVariant)GetValue(VariantProperty); set => SetValue(VariantProperty, value); }
}

