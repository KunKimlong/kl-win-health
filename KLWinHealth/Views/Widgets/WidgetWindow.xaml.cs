using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shapes;
using KLWinHealth.Services;
using KLWinHealth.Services.Widgets;
using Serilog;

namespace KLWinHealth.Views.Widgets;

/// <summary>
/// One desktop widget window. Locked: attached to the desktop layer and click-through.
/// Edit mode: still on the desktop layer but clickable; click a widget to show its outline + corner handles, drag to move (snaps to grid), right-click menu.
/// Position is stored in physical pixels (virtual-desktop coordinates) and applied with SetWindowPos (DPI-independent).
/// </summary>
public partial class WidgetWindow : Window
{
    private static readonly uint TaskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");

    private readonly WidgetManager _manager;
    private IntPtr _hwnd;
    private bool _moving;          // true while we move the window ourselves (ignore config X/Y echo)
    private bool _closingByApp;

    // corner-handle scaling state
    private string? _handle;
    private Native.RECT _startRect;
    private Native.POINT _startCursor;
    private double _startScale;

    public WidgetModel Model { get; }
    public IntPtr Handle => _hwnd;
    public bool ClosedUnexpectedly { get; private set; }

    public WidgetWindow(WidgetModel model, WidgetManager manager)
    {
        InitializeComponent();
        Model = model;
        _manager = manager;
        DataContext = model;
        Left = -32000; // off-screen until SourceInitialized places it in physical pixels
        Top = -32000;

        Model.Config.PropertyChanged += OnConfigChanged;
        Root.MouseLeftButtonDown += OnBodyMouseDown;
        foreach (var h in new[] { HandleTL, HandleTR, HandleBL, HandleBR })
        {
            h.MouseLeftButtonDown += OnHandleDown;
            h.MouseMove += OnHandleMove;
            h.MouseLeftButtonUp += OnHandleUp;
        }
        Menu.Opened += (_, _) => { if (!Model.IsEditMode) Menu.IsOpen = false; else SetSelected(true); };
        Closed += (_, _) =>
        {
            Model.Config.PropertyChanged -= OnConfigChanged;
            if (!_closingByApp) ClosedUnexpectedly = true;
            _clickWatch?.Stop();
            if (_selected == this) _selected = null;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        MoveToConfig();
        ApplyMode();
    }

    public void CloseByApp()
    {
        _closingByApp = true;
        try { Close(); } catch { }
    }

    // ------------------------------------------------------------------ mode
    public void ApplyMode()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Both modes live on the desktop layer (behind every app window).
        // Edit mode only makes the widget clickable so it can be dragged / resized on the desktop.
        Topmost = false;
        DesktopLayer.SetClickThrough(_hwnd, !Model.IsEditMode);
        DesktopLayer.Attach(_hwnd, _manager.Desktop);
        if (!Model.IsEditMode) SetSelected(false);
    }

    // ------------------------------------------------------------------ selection (outline only on the clicked widget)
    private static WidgetWindow? _selected;
    private System.Windows.Threading.DispatcherTimer? _clickWatch;
    private bool _buttonWasDown;

    private void SetSelected(bool on)
    {
        on &= Model.IsEditMode;
        if (on && _selected != this)
        {
            _selected?.SetSelected(false);
            _selected = this;
        }
        else if (!on && _selected == this) _selected = null;
        EditOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // While selected, watch for a click anywhere outside this widget (desktop, taskbar, another app) to deselect.
        // Widgets never take focus, so there is no "lost focus" event — a light 50 ms poll is used, only while selected.
        if (on)
        {
            _buttonWasDown = true; // ignore the click that selected it
            _clickWatch ??= CreateClickWatch();
            _clickWatch.Start();
        }
        else _clickWatch?.Stop();
    }

    private System.Windows.Threading.DispatcherTimer CreateClickWatch()
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        t.Tick += (_, _) =>
        {
            bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0
                     || (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
            bool pressed = down && !_buttonWasDown;
            _buttonWasDown = down;
            if (!pressed || _handle != null || Menu.IsOpen) return;
            if (Native.GetCursorPos(out var p) && Native.GetWindowRect(_hwnd, out var r)
                && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return; // click on this widget
            SetSelected(false);
        };
        return t;
    }

    public void Reattach()
    {
        if (_hwnd != IntPtr.Zero) DesktopLayer.Attach(_hwnd, _manager.Desktop);
    }

    // ------------------------------------------------------------------ position
    private void MoveToConfig()
    {
        if (_hwnd == IntPtr.Zero) return;
        var (x, y) = WidgetManager.EnsureOnScreen(Model.Config.X, Model.Config.Y);
        _moving = true;
        try
        {
            if (x != Model.Config.X) Model.Config.X = x;
            if (y != Model.Config.Y) Model.Config.Y = y;
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        finally { _moving = false; }
    }

    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_moving) return;
        if (e.PropertyName is nameof(Model.Config.X) or nameof(Model.Config.Y)) MoveToConfig();
    }

    private void StorePosition(bool snap)
    {
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        int x = r.Left, y = r.Top;
        if (snap && _manager.SnapToGrid)
        {
            var g = Math.Max(2, _manager.GridSize);
            x = (int)Math.Round(x / (double)g) * g;
            y = (int)Math.Round(y / (double)g) * g;
        }
        _moving = true;
        try
        {
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Model.Config.X = x;
            Model.Config.Y = y;
        }
        finally { _moving = false; }
    }

    private void OnBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Model.IsEditMode || e.OriginalSource is Rectangle { Tag: string }) return;
        SetSelected(true);
        try { DragMove(); } catch (InvalidOperationException) { }
        StorePosition(snap: true);
        DesktopLayer.PushToBottom(_hwnd);
    }

    // ------------------------------------------------------------------ corner scaling
    private void OnHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (!Model.IsEditMode || sender is not Rectangle r) return;
        _handle = r.Tag as string;
        Native.GetWindowRect(_hwnd, out _startRect);
        Native.GetCursorPos(out _startCursor);
        _startScale = Model.Config.Scale;
        r.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMove(object sender, MouseEventArgs e)
    {
        if (_handle == null || e.LeftButton != MouseButtonState.Pressed) return;
        Native.GetCursorPos(out var p);
        double w = Math.Max(1, _startRect.Right - _startRect.Left);
        double dx = p.X - _startCursor.X;
        if (_handle is "TL" or "BL") dx = -dx;
        var scale = Math.Round(Math.Clamp(_startScale * (w + dx) / w, 0.5, 3.0) / 0.05) * 0.05;
        if (Math.Abs(scale - Model.Config.Scale) < 0.001) return;
        Model.Config.Scale = scale;
        UpdateLayout();

        // Keep the opposite corner where it was.
        Native.GetWindowRect(_hwnd, out var now);
        int nw = now.Right - now.Left, nh = now.Bottom - now.Top;
        int x = _handle is "TL" or "BL" ? _startRect.Right - nw : _startRect.Left;
        int y = _handle is "TL" or "TR" ? _startRect.Bottom - nh : _startRect.Top;
        _moving = true;
        try { Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE); }
        finally { _moving = false; }
    }

    private void OnHandleUp(object sender, MouseButtonEventArgs e)
    {
        if (_handle == null) return;
        _handle = null;
        (sender as UIElement)?.ReleaseMouseCapture();
        StorePosition(snap: false);
        DesktopLayer.PushToBottom(_hwnd);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ menu
    private void OnEditClick(object sender, RoutedEventArgs e) => _manager.RequestEdit(Model.Config);
    private void OnDuplicateClick(object sender, RoutedEventArgs e) => _manager.Duplicate(Model.Config);
    private void OnResetSizeClick(object sender, RoutedEventArgs e) => Model.Config.Scale = 1.0;
    private void OnRemoveClick(object sender, RoutedEventArgs e) => _manager.Remove(Model.Config);
    private void OnLockClick(object sender, RoutedEventArgs e) => _manager.SetLocked(true);

    // ------------------------------------------------------------------ messages
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarCreatedMsg && TaskbarCreatedMsg != 0)
        {
            Log.Information("Explorer restarted (TaskbarCreated) — re-attaching widgets");
            Dispatcher.BeginInvoke(_manager.ReattachAll);
        }
        else if (msg == Native.WM_DISPLAYCHANGE)
        {
            Dispatcher.BeginInvoke(MoveToConfig);
        }
        return IntPtr.Zero;
    }
}
