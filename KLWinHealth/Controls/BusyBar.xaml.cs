using System.Windows.Controls;

namespace KLWinHealth.Controls;

/// <summary>Progress bar + status text + Cancel button for long operations. DataContext = BusyState.</summary>
public partial class BusyBar : UserControl
{
    public BusyBar() => InitializeComponent();
}
