using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KLWinHealth.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) => AnimateTiles();
    }

    /// <summary>Tiles fade in and slide up 12px, staggered 60ms each.</summary>
    private void AnimateTiles()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        int i = 0;
        foreach (UIElement tile in Tiles.Children)
        {
            var begin = TimeSpan.FromMilliseconds(60 * i++);
            var duration = TimeSpan.FromMilliseconds(260);
            // Animate a wrapper transform on the tile itself (the template animates its inner Root for hover).
            var tt = new TranslateTransform(0, 12);
            tile.RenderTransform = tt;
            tile.Opacity = 0;
            tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, duration) { BeginTime = begin, EasingFunction = ease });
        }
    }
}
