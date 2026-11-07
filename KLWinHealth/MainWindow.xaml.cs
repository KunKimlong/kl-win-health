using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using KLWinHealth.Services;
using KLWinHealth.ViewModels;
using Serilog;

namespace KLWinHealth;

/// <summary>Implemented by pages that accept files dropped onto the window.</summary>
public interface IFileDropTarget
{
    void OnFilesDropped(string[] paths);
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsService _settings;

    /// <summary>Set by App when the user chose Exit — closing then really closes.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when files are dropped and the current page doesn't handle them.</summary>
    public event Action<string[]>? UnhandledFilesDropped;

    public MainWindow(MainViewModel vm, SettingsService settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;
        StateChanged += (_, _) => FixMaximizedMargin();
        SourceInitialized += OnSourceInitialized;
    }

    private void FixMaximizedMargin()
    {
        // WindowChrome windows overhang the screen by the resize border when maximized.
        var t = SystemParameters.WindowResizeBorderThickness;
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4)
            : new Thickness(0);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);

        // Drag & drop from (non-elevated) Explorer into this elevated window: OLE drag-drop is blocked by UIPI,
        // so use classic WM_DROPFILES and allow those messages through the filter.
        try
        {
            Native.RevokeDragDrop(hwnd);
            Native.ChangeWindowMessageFilterEx(hwnd, Native.WM_DROPFILES, Native.MSGFLT_ALLOW, IntPtr.Zero);
            Native.ChangeWindowMessageFilterEx(hwnd, Native.WM_COPYDATA, Native.MSGFLT_ALLOW, IntPtr.Zero);
            Native.ChangeWindowMessageFilterEx(hwnd, Native.WM_COPYGLOBALDATA, Native.MSGFLT_ALLOW, IntPtr.Zero);
            Native.DragAcceptFiles(hwnd, true);
        }
        catch (Exception ex) { Log.Warning(ex, "Could not enable drag & drop"); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_DROPFILES)
        {
            handled = true;
            var files = new List<string>();
            try
            {
                var count = Native.DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    var len = Native.DragQueryFile(wParam, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    Native.DragQueryFile(wParam, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
            }
            finally { Native.DragFinish(wParam); }

            if (files.Count > 0)
            {
                Log.Information("{Count} item(s) dropped on the window", files.Count);
                if (_vm.Navigation.CurrentPage is IFileDropTarget target) target.OnFilesDropped(files.ToArray());
                else UnhandledFilesDropped?.Invoke(files.ToArray());
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _settings.Current.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            Log.Debug("Main window hidden to tray");
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!AllowClose) ((App)Application.Current).ExitApp();
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
