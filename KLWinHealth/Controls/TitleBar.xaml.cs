using System.Windows;
using System.Windows.Controls;
using KLWinHealth.Services;
using MahApps.Metro.IconPacks;

namespace KLWinHealth.Controls;

/// <summary>Custom title bar for WindowChrome windows: app name left, min / max / close right.</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        AppTitle.Text = LetterSpace("KL WIN HEALTH");
        LimitedBadge.Visibility = ShellHelper.IsAdministrator() ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) =>
        {
            var w = Window.GetWindow(this);
            if (w == null) return;
            w.StateChanged += (_, _) => UpdateMaxIcon(w);
            UpdateMaxIcon(w);
        };
    }

    /// <summary>Uppercase with letter spacing: hair spaces between letters, wider gaps between words.</summary>
    public static string LetterSpace(string s) =>
        string.Join(" ", s.ToUpperInvariant().ToCharArray()).Replace("   ", " ");

    private void UpdateMaxIcon(Window w) =>
        MaxIcon.Kind = w.WindowState == WindowState.Maximized ? PackIconMaterialKind.WindowRestore : PackIconMaterialKind.WindowMaximize;

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w != null) SystemCommands.MinimizeWindow(w);
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w == null) return;
        if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w);
        else SystemCommands.MaximizeWindow(w);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();

    private void OnRestartAsAdmin(object sender, System.Windows.Input.MouseButtonEventArgs e) => App.RestartAsAdmin();
}
