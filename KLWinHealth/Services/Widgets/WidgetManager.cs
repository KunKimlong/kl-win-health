using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using KLWinHealth.Models;
using KLWinHealth.Views.Widgets;
using Serilog;

namespace KLWinHealth.Services.Widgets;

/// <summary>
/// Owns the desktop widget windows: creates them from settings, keeps them attached to the desktop layer,
/// switches edit / locked mode, saves changes (debounced) and re-creates widgets after Explorer restarts.
/// </summary>
public class WidgetManager
{
    private readonly SettingsService _settings;
    private readonly SystemStatsService _stats;
    private readonly Dictionary<string, WidgetWindow> _windows = new();
    private readonly Dictionary<string, WidgetModel> _models = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(5) };
    private IDisposable? _extendedStats;
    private bool _started;

    public ObservableCollection<WidgetConfig> Widgets { get; } = new();
    public DesktopLayer.DesktopInfo Desktop { get; private set; } = new(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, DesktopLayer.Layout.Unknown);

    public bool IsLocked => _settings.Current.WidgetsLocked;
    public bool SnapToGrid => _settings.Current.WidgetsSnapToGrid;
    public int GridSize => _settings.Current.WidgetsGridSize;

    public event Action<bool>? LockedChanged;
    /// <summary>Raised when "Edit in KL Win Health" is chosen on a widget.</summary>
    public event Action<WidgetConfig>? EditRequested;

    public WidgetManager(SettingsService settings, SystemStatsService stats)
    {
        _settings = settings;
        _stats = stats;
        _tick.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            foreach (var m in _models.Values) m.Tick(now);
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        _watchdog.Tick += (_, _) => Watchdog();
    }

    // ------------------------------------------------------------------ lifecycle
    public void Start()
    {
        if (_started) return;
        _started = true;
        Desktop = DesktopLayer.Find();
        Log.Information("Desktop layer: {Layout} (progman={P:X}, iconHost={H:X}, workerW={W:X})",
            Desktop.Layout, Desktop.Progman.ToInt64(), Desktop.IconHost.ToInt64(), Desktop.WorkerW.ToInt64());
        foreach (var c in _settings.Current.Widgets)
        {
            Widgets.Add(c);
            CreateWindow(c);
        }
        UpdateStatsRequest();
        _tick.Start();
        _watchdog.Start();
        Log.Information("Widgets started: {Count} widget(s), locked={Locked}", Widgets.Count, IsLocked);
    }

    public void Stop()
    {
        _tick.Stop();
        _watchdog.Stop();
        if (_saveTimer.IsEnabled) { _saveTimer.Stop(); Save(); }
        foreach (var w in _windows.Values.ToList()) w.CloseByApp();
        _windows.Clear();
        foreach (var m in _models.Values) m.Detach();
        _models.Clear();
        _extendedStats?.Dispose();
        _extendedStats = null;
        _started = false;
    }

    private void CreateWindow(WidgetConfig c)
    {
        try
        {
            var model = EnsureModel(c);
            model.IsEditMode = !IsLocked;
            var w = new WidgetWindow(model, this);
            _windows[c.Id] = w;
            w.Show();
        }
        catch (Exception ex) { Log.Error(ex, "Creating widget {Type} failed", c.Type); }
    }

    private WidgetModel EnsureModel(WidgetConfig c)
    {
        if (!_models.TryGetValue(c.Id, out var model))
        {
            model = new WidgetModel(c, _stats);
            _models[c.Id] = model;
            c.PropertyChanged += OnConfigChanged;
        }
        return model;
    }

    public WidgetModel? GetModel(WidgetConfig c) => _models.TryGetValue(c.Id, out var m) ? m : null;

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

#if KL_DEV_NOELEVATE
    private bool _devPreview;

    /// <summary>Dev snapshots only: one widget of each type, in memory (no desktop windows, nothing saved).</summary>
    public void DevPreview()
    {
        _devPreview = true;
        Desktop = DesktopLayer.Find();
        Log.Information("[dev] Desktop layer: {Layout} (progman={P:X}, iconHost={H:X}, workerW={W:X})",
            Desktop.Layout, Desktop.Progman.ToInt64(), Desktop.IconHost.ToInt64(), Desktop.WorkerW.ToInt64());
        var order = new List<WidgetType> { WidgetType.SystemMonitor, WidgetType.Clock, WidgetType.Calendar, WidgetType.NetworkSpeed, WidgetType.Battery, WidgetType.Text };
        if (Enum.TryParse<WidgetType>(Environment.GetEnvironmentVariable("KLDEV_WIDGET_FIRST"), out var first)) { order.Remove(first); order.Insert(0, first); }
        foreach (var t in order)
        {
            var c = new WidgetConfig { Type = t, FontSize = t == WidgetType.Clock ? 18 : 14 };
            Widgets.Add(c);
            EnsureModel(c);
        }
        UpdateStatsRequest();
        _tick.Start();
    }

    /// <summary>Dev only: creates one real, locked widget window (nothing saved) and reports how it was attached.</summary>
    public async Task<string> DevWindowTestAsync()
    {
        _devPreview = true;
        _settings.Current.WidgetsLocked = Environment.GetEnvironmentVariable("KLDEV_WIDGET_EDIT") != "1";
        Desktop = DesktopLayer.Find();
        var c = new WidgetConfig { Type = WidgetType.Clock, X = 120, Y = 120, FontSize = 16 };
        Widgets.Add(c);
        CreateWindow(c);
        _tick.Start();
        await Task.Delay(2500);
        var w = _windows[c.Id];
        Native.GetWindowRect(w.Handle, out var r);
        var ex = Native.GetWindowLongPtr(w.Handle, Native.GWL_EXSTYLE).ToInt64();
        var report = string.Join(Environment.NewLine,
            $"layout={Desktop.Layout} progman={Desktop.Progman} iconHost={Desktop.IconHost} workerW={Desktop.WorkerW}",
            $"hwnd={w.Handle} attached={DesktopLayer.IsAttached(w.Handle, Desktop)}",
            $"clickThrough={(ex & Native.WS_EX_TRANSPARENT) != 0} toolWindow={(ex & Native.WS_EX_TOOLWINDOW) != 0} noActivate={(ex & Native.WS_EX_NOACTIVATE) != 0}",
            $"rect={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top} topmost={w.Topmost}");
        // Simulate an Explorer-side detach and check that re-attaching works (no topmost / edit mode: never covers the user's apps).
        DesktopLayer.Detach(w.Handle);
        var detached = DesktopLayer.IsAttached(w.Handle, Desktop);
        ReattachAll();
        report += Environment.NewLine + $"detach test: afterDetach={detached} afterReattach={DesktopLayer.IsAttached(w.Handle, Desktop)}";
        return report;
    }
#endif

    private void Save()
    {
#if KL_DEV_NOELEVATE
        if (_devPreview) return;
#endif
        _settings.Current.Widgets = Widgets.ToList();
        _settings.Save();
    }

    private void UpdateStatsRequest()
    {
        var needs = Widgets.Any(w => w.Type is WidgetType.SystemMonitor or WidgetType.NetworkSpeed);
        if (needs && _extendedStats == null) _extendedStats = _stats.RequestExtendedStats();
        else if (!needs && _extendedStats != null) { _extendedStats.Dispose(); _extendedStats = null; }
    }

    // ------------------------------------------------------------------ add / remove
    public WidgetConfig Add(WidgetType type)
    {
        var (x, y) = NextFreeSpot();
        var c = new WidgetConfig { Type = type, X = x, Y = y };
        switch (type)
        {
            case WidgetType.Clock: c.FontSize = 18; break;
            case WidgetType.Calendar: c.FontSize = 14; break;
            case WidgetType.SystemMonitor: c.FontSize = 14; break;
            case WidgetType.NetworkSpeed: c.FontSize = 14; break;
            case WidgetType.Battery: c.FontSize = 14; break;
            case WidgetType.Text: c.FontSize = 22; c.BackgroundOpacity = 0; break;
        }
        AddConfig(c);
        Log.Information("Widget added: {Type}", type);
        return c;
    }

    public WidgetConfig Duplicate(WidgetConfig source)
    {
        var c = source.Clone();
        c.X += 40;
        c.Y += 40;
        AddConfig(c);
        return c;
    }

    private void AddConfig(WidgetConfig c)
    {
        Widgets.Add(c);
        if (_started) CreateWindow(c); else EnsureModel(c);
        UpdateStatsRequest();
        Save();
    }

    public void Remove(WidgetConfig c)
    {
        if (_windows.Remove(c.Id, out var w)) w.CloseByApp();
        if (_models.Remove(c.Id, out var m)) m.Detach();
        c.PropertyChanged -= OnConfigChanged;
        Widgets.Remove(c);
        UpdateStatsRequest();
        Save();
        Log.Information("Widget removed: {Type}", c.Type);
    }

    public void RequestEdit(WidgetConfig c) => EditRequested?.Invoke(c);

    // ------------------------------------------------------------------ modes
    public void SetLocked(bool locked)
    {
        _settings.Current.WidgetsLocked = locked;
        _settings.Save();
        if (locked) Desktop = DesktopLayer.Find();
        foreach (var m in _models.Values) m.IsEditMode = !locked;
        foreach (var w in _windows.Values) w.ApplyMode();
        Log.Information("Widgets {Mode}", locked ? "locked" : "edit mode");
        LockedChanged?.Invoke(locked);
    }

    public void SetSnap(bool snap, int grid)
    {
        _settings.Current.WidgetsSnapToGrid = snap;
        _settings.Current.WidgetsGridSize = Math.Clamp(grid, 2, 200);
        _settings.Save();
    }

    public void ReattachAll()
    {
        Desktop = DesktopLayer.Find();
        foreach (var c in Widgets.ToList())
        {
            if (!_windows.TryGetValue(c.Id, out var w) || w.ClosedUnexpectedly) CreateWindow(c);
            else w.Reattach();
        }
    }

    /// <summary>
    /// Explorer can restart (crash, update) and take owned windows with it. Re-create / re-attach when that happens.
    /// </summary>
    private void Watchdog()
    {
        try
        {
            var progman = Native.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return; // Explorer not running right now; try again later
            bool lost = Widgets.Any(c => !_windows.TryGetValue(c.Id, out var w) || w.ClosedUnexpectedly);
            bool moved = progman != Desktop.Progman;
            bool detached = _windows.Values.Any(w => w.Handle != IntPtr.Zero && !DesktopLayer.IsAttached(w.Handle, Desktop));
            if (!lost && !moved && !detached) return;
            Log.Information("Widget watchdog: re-attaching (lost={Lost}, explorer changed={Moved})", lost, moved);
            ReattachAll();
        }
        catch (Exception ex) { Log.Debug(ex, "Widget watchdog failed"); }
    }

    // ------------------------------------------------------------------ geometry helpers
    /// <summary>Moves a point that's on no monitor (monitor unplugged) back onto the primary monitor.</summary>
    public static (int x, int y) EnsureOnScreen(int x, int y)
    {
        var pt = new Native.POINT(x + 20, y + 20);
        if (Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero) return (x, y);
        var mon = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfo(mon, ref info)) return (info.rcWork.Left + 80, info.rcWork.Top + 80);
        return (100, 100);
    }

    private (int x, int y) NextFreeSpot()
    {
        var mon = Native.MonitorFromPoint(new Native.POINT(0, 0), Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        int left = 100, top = 100, right = 1800;
        if (Native.GetMonitorInfo(mon, ref info)) { left = info.rcWork.Left + 120; top = info.rcWork.Top + 120; right = info.rcWork.Right - 300; }
        // Cascade so new widgets don't land exactly on top of each other.
        int x = left, y = top;
        for (int i = 0; i < 30 && Widgets.Any(w => Math.Abs(w.X - x) < 30 && Math.Abs(w.Y - y) < 30); i++)
        {
            x += 40; y += 40;
            if (x > right) { x = left; }
        }
        return (x, y);
    }
}
