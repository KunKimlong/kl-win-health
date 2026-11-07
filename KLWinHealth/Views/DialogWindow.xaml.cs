using System.Windows;
using System.Windows.Input;
using MahApps.Metro.IconPacks;

namespace KLWinHealth.Views;

public enum DialogKind { Info, Confirm, Danger, Error, Success }

/// <summary>Dark, themed replacement for MessageBox.</summary>
public partial class DialogWindow : Window
{
    public DialogWindow(DialogKind kind, string title, string message, IEnumerable<string>? details,
        string okText, string? cancelText, string? warning)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        OkButton.Content = okText;

        if (cancelText == null) CancelButton.Visibility = Visibility.Collapsed;
        else CancelButton.Content = cancelText;

        var list = details?.ToList();
        if (list is { Count: > 0 })
        {
            DetailsList.ItemsSource = list;
            DetailsHost.Visibility = Visibility.Visible;
        }

        if (!string.IsNullOrWhiteSpace(warning))
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }

        switch (kind)
        {
            case DialogKind.Danger:
                HeaderIcon.Kind = PackIconMaterialKind.AlertOutline;
                HeaderIcon.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                OkButton.Style = (Style)FindResource("DangerButton");
                break;
            case DialogKind.Error:
                HeaderIcon.Kind = PackIconMaterialKind.AlertCircleOutline;
                HeaderIcon.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                TitleText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                Frame.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");
                break;
            case DialogKind.Confirm:
                HeaderIcon.Kind = PackIconMaterialKind.HelpCircleOutline;
                break;
            case DialogKind.Success:
                HeaderIcon.Kind = PackIconMaterialKind.CheckCircleOutline;
                break;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDragTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
