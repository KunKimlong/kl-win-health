using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KLWinHealth.Models;

namespace KLWinHealth.Controls;

/// <summary>
/// A border whose top-left and bottom-right corners are cut at 45° (ROG style). Drawn with a PathGeometry,
/// not a rounded rectangle. Also supports Sharp and Rounded corners (used by desktop widgets).
/// </summary>
public class AngledBorder : Decorator
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(AngledBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

    public static readonly DependencyProperty BorderBrushProperty = DependencyProperty.Register(
        nameof(BorderBrush), typeof(Brush), typeof(AngledBorder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.SubPropertiesDoNotAffectRender));

    public static readonly DependencyProperty BorderThicknessProperty = DependencyProperty.Register(
        nameof(BorderThickness), typeof(double), typeof(AngledBorder),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CutSizeProperty = DependencyProperty.Register(
        nameof(CutSize), typeof(double), typeof(AngledBorder),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty CornerStyleProperty = DependencyProperty.Register(
        nameof(CornerStyle), typeof(CornerStyle), typeof(AngledBorder),
        new FrameworkPropertyMetadata(CornerStyle.Angled, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(AngledBorder),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(
        nameof(ClipContent), typeof(bool), typeof(AngledBorder),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange));

    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush? BorderBrush { get => (Brush?)GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => (double)GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double CutSize { get => (double)GetValue(CutSizeProperty); set => SetValue(CutSizeProperty, value); }
    public CornerStyle CornerStyle { get => (CornerStyle)GetValue(CornerStyleProperty); set => SetValue(CornerStyleProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    /// <summary>Builds the outline for a rectangle: TL and BR corners cut at 45° by <paramref name="cut"/>.</summary>
    public static Geometry BuildGeometry(Rect r, double cut, CornerStyle style)
    {
        if (r.Width <= 0 || r.Height <= 0) return Geometry.Empty;
        cut = Math.Max(0, Math.Min(cut, Math.Min(r.Width, r.Height) / 2));
        switch (style)
        {
            case CornerStyle.Sharp:
                return new RectangleGeometry(r);
            case CornerStyle.Rounded:
                return new RectangleGeometry(r, cut, cut);
            default:
                var fig = new PathFigure { StartPoint = new Point(r.Left + cut, r.Top), IsClosed = true, IsFilled = true };
                fig.Segments.Add(new PolyLineSegment(new[]
                {
                    new Point(r.Right, r.Top),
                    new Point(r.Right, r.Bottom - cut),
                    new Point(r.Right - cut, r.Bottom),
                    new Point(r.Left, r.Bottom),
                    new Point(r.Left, r.Top + cut)
                }, true));
                var g = new PathGeometry();
                g.Figures.Add(fig);
                g.Freeze();
                return g;
        }
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var inset = Inset();
        var child = Child;
        if (child == null) return new Size(inset.Left + inset.Right, inset.Top + inset.Bottom);
        child.Measure(new Size(
            Math.Max(0, constraint.Width - inset.Left - inset.Right),
            Math.Max(0, constraint.Height - inset.Top - inset.Bottom)));
        return new Size(child.DesiredSize.Width + inset.Left + inset.Right, child.DesiredSize.Height + inset.Top + inset.Bottom);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var child = Child;
        if (child != null)
        {
            var inset = Inset();
            var rect = new Rect(inset.Left, inset.Top,
                Math.Max(0, arrangeSize.Width - inset.Left - inset.Right),
                Math.Max(0, arrangeSize.Height - inset.Top - inset.Bottom));
            child.Arrange(rect);
            if (ClipContent)
            {
                var b = BorderThickness;
                var clip = BuildGeometry(new Rect(b, b, Math.Max(0, arrangeSize.Width - 2 * b), Math.Max(0, arrangeSize.Height - 2 * b)),
                    Math.Max(0, CutSize - b / 2), CornerStyle).Clone();
                clip.Transform = new TranslateTransform(-rect.Left, -rect.Top);
                child.Clip = clip;
            }
            else if (child.Clip != null)
            {
                child.ClearValue(ClipProperty);
            }
        }
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var b = BorderThickness;
        var brush = BorderBrush;
        var half = brush != null && b > 0 ? b / 2 : 0;
        var rect = new Rect(half, half, Math.Max(0, ActualWidth - 2 * half), Math.Max(0, ActualHeight - 2 * half));
        var geo = BuildGeometry(rect, CutSize, CornerStyle);
        var pen = brush != null && b > 0 ? new Pen(brush, b) { LineJoin = PenLineJoin.Miter } : null;
        dc.DrawGeometry(Background, pen, geo);
    }

    private Thickness Inset()
    {
        var b = BorderThickness;
        var p = Padding;
        return new Thickness(p.Left + b, p.Top + b, p.Right + b, p.Bottom + b);
    }
}
