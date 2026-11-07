using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KLWinHealth.Views;

public partial class VirusCheckView : UserControl
{
    public VirusCheckView() => InitializeComponent();

    /// <summary>"Scan entire PC" opens its Quick / Full menu under the button on a normal left click.</summary>
    private void OnScanPcClick(object sender, RoutedEventArgs e)
    {
        if (ScanPcButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ScanPcButton;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }
}
