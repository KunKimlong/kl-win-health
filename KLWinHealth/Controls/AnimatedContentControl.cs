using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KLWinHealth.Controls;

/// <summary>ContentControl that fades + slides new content in (200ms) — used for page transitions.</summary>
public class AnimatedContentControl : ContentControl
{
    private readonly TranslateTransform _translate = new();

    public AnimatedContentControl()
    {
        RenderTransform = _translate;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent == null) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        _translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }
}
