using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.IconPacks;

namespace KLWinHealth.Controls;

/// <summary>
/// Module page header: Back button (top-left, bound to the page's GoBackCommand), icon, title, subtitle,
/// and right-aligned Content for page actions (e.g. Restore). Template in RogGreen.xaml.
/// </summary>
public class PageHeader : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(""));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(null));
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(PackIconMaterialKind), typeof(PageHeader), new PropertyMetadata(PackIconMaterialKind.Apps));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public PackIconMaterialKind Icon { get => (PackIconMaterialKind)GetValue(IconProperty); set => SetValue(IconProperty, value); }
}
