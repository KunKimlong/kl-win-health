using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using MahApps.Metro.IconPacks;

namespace KLWinHealth.Services;

/// <summary>System tray icon with menu: Open, Wallpaper slideshow, Widgets edit/lock, Exit.</summary>
public class TrayService : IDisposable
{
    private TaskbarIcon? _icon;
    private MenuItem? _editItem;
    private MenuItem? _lockItem;
    private MenuItem? _nextWallpaperItem;
    private MenuItem? _slideshowItem;
    private Separator? _slideshowSeparator;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action<bool>? WidgetsLockRequested; // true = lock, false = edit
    public event Action? NextWallpaperRequested;
    public event Action? SlideshowToggleRequested;

    public void Initialize(bool widgetsLocked)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open KL Win Health", PackIconMaterialKind.OpenInApp, () => OpenRequested?.Invoke(), bold: true));
        menu.Items.Add(new Separator());
        _nextWallpaperItem = Item("Next wallpaper", PackIconMaterialKind.SkipNext, () => NextWallpaperRequested?.Invoke());
        _slideshowItem = Item("Wallpaper slideshow", PackIconMaterialKind.ImageMultiple, () => SlideshowToggleRequested?.Invoke());
        _slideshowSeparator = new Separator();
        menu.Items.Add(_nextWallpaperItem);
        menu.Items.Add(_slideshowItem);
        menu.Items.Add(_slideshowSeparator);
        SetSlideshowState(false, false);
        _editItem = Item("Widgets: edit mode", PackIconMaterialKind.CursorMove, () => WidgetsLockRequested?.Invoke(false));
        _lockItem = Item("Widgets: locked", PackIconMaterialKind.Lock, () => WidgetsLockRequested?.Invoke(true));
        menu.Items.Add(_editItem);
        menu.Items.Add(_lockItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", PackIconMaterialKind.Power, () => ExitRequested?.Invoke()));

        _icon = new TaskbarIcon
        {
            ToolTipText = "KL Win Health",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick
        };
        _icon.TrayLeftMouseUp += (_, _) => OpenRequested?.Invoke();
        _icon.ForceCreate(false);
        SetWidgetsLocked(widgetsLocked);
    }

    public void SetWidgetsLocked(bool locked)
    {
        if (_editItem != null) _editItem.IsChecked = !locked;
        if (_lockItem != null) _lockItem.IsChecked = locked;
    }

    /// <summary>Shows the slideshow items only when a slideshow folder is configured.</summary>
    public void SetSlideshowState(bool configured, bool running)
    {
        var vis = configured ? Visibility.Visible : Visibility.Collapsed;
        if (_nextWallpaperItem != null) _nextWallpaperItem.Visibility = vis;
        if (_slideshowSeparator != null) _slideshowSeparator.Visibility = vis;
        if (_slideshowItem != null)
        {
            _slideshowItem.Visibility = vis;
            _slideshowItem.IsChecked = running;
        }
    }

    public void ShowNotification(string title, string message)
    {
        try { _icon?.ShowNotification(title, message); } catch { /* notifications are best-effort */ }
    }

    private static MenuItem Item(string header, PackIconMaterialKind icon, Action onClick, bool bold = false)
    {
        var mi = new MenuItem
        {
            Header = header,
            Icon = new PackIconMaterial { Kind = icon, Width = 13, Height = 13, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush") }
        };
        if (bold) mi.FontWeight = FontWeights.SemiBold;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
