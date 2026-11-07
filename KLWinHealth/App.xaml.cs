using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using KLWinHealth.Services;
using KLWinHealth.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KLWinHealth;

public partial class App : Application
{
#if KL_DEV_NOELEVATE
    // Developer smoke-test build: separate single-instance names so it never collides with a real install.
    private const string MutexName = @"Global\KLWinHealth.SingleInstance.dev";
    private const string ShowEventName = @"Global\KLWinHealth.ShowWindow.dev";
#else
    private const string MutexName = @"Global\KLWinHealth.SingleInstance.v1";
    private const string ShowEventName = @"Global\KLWinHealth.ShowWindow.v1";
#endif

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _exiting;

    public static IServiceProvider Services { get; private set; } = null!;
    public static bool IsAdmin { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();

        AppPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppPaths.Logs, "klwinhealth-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30,
                restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Fatal(ex.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error(ex.Exception, "Unobserved task exception"); ex.SetObserved(); };

        IsAdmin = ShellHelper.IsAdministrator();
        Log.Information("KL Win Health {Version} starting (admin={Admin}, args={Args})",
            typeof(App).Assembly.GetName().Version, IsAdmin, string.Join(' ', e.Args));

#if !KL_DEV_NOELEVATE
        // Not elevated: start the elevated copy through the launch task (no UAC), or ask UAC once to install it.
        var uninstall = args.Contains("--uninstall");
        if (!IsAdmin && !args.Contains("--autoclean"))
        {
            if (await Installer.HandOffAsync(e.Args, forceUac: uninstall))
            {
                Log.CloseAndFlush();
                Shutdown(0);
                return;
            }
            if (uninstall) { Shutdown(1); return; }
            // UAC cancelled: carry on in limited mode.
        }
#endif

        Services = BuildServices();
        var settings = Services.GetRequiredService<SettingsService>();
        settings.Load();
        Services.GetRequiredService<ConfigService>().Load();

        // Headless weekly auto-clean (started by Task Scheduler).
        if (args.Contains("--autoclean"))
        {
            var code = await AutoCleanRunner.RunAsync(Services);
            Log.CloseAndFlush();
            Shutdown(code);
            return;
        }

#if !KL_DEV_NOELEVATE
        if (uninstall)
        {
            await RunUninstallAsync();
            return;
        }
#endif

        // Single instance: a second launch just brings the first one to the front.
        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            Log.Information("Another instance is running; asking it to show its window");
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Log.CloseAndFlush();
            Shutdown(0);
            return;
        }

#if !KL_DEV_NOELEVATE
        if (IsAdmin)
        {
            var scheduler = Services.GetRequiredService<SchedulerService>();
            if (!Installer.IsRunningInstalled)
            {
                // First run / update: install to Program Files, then hand over to the installed copy.
                if (await Installer.InstallAsync(scheduler, settings))
                {
                    try { _mutex.ReleaseMutex(); } catch { }
                    _mutex.Dispose();
                    _mutex = null;
                    Process.Start(new ProcessStartInfo(Installer.InstalledExe,
                        string.Join(' ', e.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)))
                    { UseShellExecute = false, WorkingDirectory = Installer.InstallDir });
                    Log.Information("Handed over to the installed copy");
                    Log.CloseAndFlush();
                    Shutdown(0);
                    return;
                }
            }
            else
            {
                _ = Installer.EnsureLaunchTaskAsync(scheduler);
                _ = Installer.EnsureStartupTaskAsync(scheduler, settings);
            }
        }
#endif
        _showEvent =new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting) break;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        var main = Services.GetRequiredService<MainWindow>();
        MainWindow = main;
        Services.GetRequiredService<NavigationService>().GoHome();

        var tray = Services.GetRequiredService<TrayService>();
        tray.OpenRequested += ShowMainWindow;
        tray.ExitRequested += ExitApp;
        tray.Initialize(settings.Current.WidgetsLocked);

        StartBackgroundFeatures();

#if KL_DEV_NOELEVATE
        // Dev-only: --devshot=<png> renders the window off-screen (no focus stealing) and exits.
        var devShot = e.Args.FirstOrDefault(a => a.StartsWith("--devshot=", StringComparison.OrdinalIgnoreCase))?[10..];
        if (!string.IsNullOrEmpty(devShot))
        {
            main.ShowActivated = false;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -20000;
            main.Top = -20000;
            var delay = int.TryParse(e.Args.FirstOrDefault(a => a.StartsWith("--devdelay=", StringComparison.OrdinalIgnoreCase))?[11..], out var d) ? d : 5;
            _ = DevSnapshotAsync(main, devShot, delay);
        }
#endif
        if (!args.Contains("--minimized")) main.Show();
        else Log.Information("Started minimized to tray");

        // --page=Virus|Rog|Windows|Browser|Storage|Wallpaper|Widgets|Settings opens a module directly.
        var page = e.Args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?[7..];
        if (!string.IsNullOrEmpty(page)) Services.GetRequiredService<HomeViewModel>().OpenCommand.Execute(page);
#if KL_DEV_NOELEVATE
        var devScan = e.Args.FirstOrDefault(a => a.StartsWith("--devscan=", StringComparison.OrdinalIgnoreCase))?[10..];
        if (!string.IsNullOrEmpty(devScan))
        {
            Services.GetRequiredService<NavigationService>().NavigateTo<VirusCheckViewModel>();
            _ = Services.GetRequiredService<VirusCheckViewModel>().ScanPathsAsync(new[] { devScan });
        }
        // Dev-only: --devtab=N selects a tab, --devcmd=Name runs <Name>Command on the current page (read-only actions only).
        var devTab = e.Args.FirstOrDefault(a => a.StartsWith("--devtab=", StringComparison.OrdinalIgnoreCase))?[9..];
        var devCmd = e.Args.FirstOrDefault(a => a.StartsWith("--devcmd=", StringComparison.OrdinalIgnoreCase))?[9..];
        if (devTab != null || devCmd != null)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(800);
                var pageVm = Services.GetRequiredService<NavigationService>().CurrentPage;
                if (pageVm == null) return;
                if (int.TryParse(devTab, out var tab)) pageVm.GetType().GetProperty("SelectedTab")?.SetValue(pageVm, tab);
                if (devCmd != null && pageVm.GetType().GetProperty(devCmd + "Command")?.GetValue(pageVm) is System.Windows.Input.ICommand cmd)
                    cmd.Execute(null);
            });
        }
#endif

        if (!IsAdmin)
        {
            Log.Warning("Not running as administrator — limited mode");
            var dialogs = Services.GetRequiredService<DialogService>();
            dialogs.Toast("Limited mode: not running as administrator. Services, policies and system folders can't be changed.", ToastKind.Warning);
        }
    }

    /// <summary>Hook point for features that run in the background (widgets, slideshow).</summary>
    partial void StartBackgroundFeaturesImpl();
    private void StartBackgroundFeatures()
    {
        try { StartBackgroundFeaturesImpl(); }
        catch (Exception ex) { Log.Error(ex, "Starting background features failed"); }
    }

    private static IServiceProvider BuildServices()
    {
        var s = new ServiceCollection();
        // Core services
        s.AddSingleton<SettingsService>();
        s.AddSingleton<ConfigService>();
        s.AddSingleton<BackupService>();
        s.AddSingleton<SchedulerService>();
        s.AddSingleton<DialogService>();
        s.AddSingleton<NavigationService>();
        s.AddSingleton<SystemStatsService>();
        s.AddSingleton<TrayService>();
        s.AddSingleton<PageContext>();
        // Windows / view models
        s.AddSingleton<MainViewModel>();
        s.AddSingleton<MainWindow>();
        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<SettingsViewModel>();
        RegisterModules(s);
        return s.BuildServiceProvider();
    }

    static partial void RegisterModules(IServiceCollection s);

    public void ShowMainWindow()
    {
        if (MainWindow is MainWindow mw) mw.BringToFront();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Information("Exiting");
        try { StopBackgroundFeaturesImpl(); } catch (Exception ex) { Log.Warning(ex, "Stopping background features failed"); }
        try { Services.GetService<TrayService>()?.Dispose(); } catch { }
        try { Services.GetService<SystemStatsService>()?.Dispose(); } catch { }
        if (MainWindow is MainWindow mw)
        {
            mw.AllowClose = true;
            mw.Close();
        }
        try { _showEvent?.Set(); } catch { }
        Log.CloseAndFlush();
        Shutdown(0);
    }

    partial void StopBackgroundFeaturesImpl();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        try
        {
            Services?.GetService<DialogService>()?.Toast("Something went wrong: " + PageViewModel.FriendlyError(e.Exception) + " (details in the log)", ToastKind.Error);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

#if KL_DEV_NOELEVATE
    private async Task DevSnapshotAsync(Window w, string file, int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        try
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            var root = (System.Windows.FrameworkElement)w.Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((System.Windows.Media.Brush)FindResource("BackgroundBrush"), null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
            }
            bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
        catch (Exception ex) { Log.Error(ex, "Dev snapshot failed"); }
        ExitApp();
    }
#endif

#if !KL_DEV_NOELEVATE
    /// <summary>--uninstall (from Apps &amp; features): removes the install, keeps the data folder.</summary>
    private async Task RunUninstallAsync()
    {
        if (Mutex.TryOpenExisting(MutexName, out var running))
        {
            running.Dispose();
            MessageBox.Show("KL Win Health is running. Exit it from the tray icon (right-click → Exit), then uninstall again.",
                "Uninstall KL Win Health", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        if (MessageBox.Show("Remove KL Win Health from this PC?\n\nYour backups, quarantine and settings in\n" + AppPaths.Root + "\nare kept.",
                "Uninstall KL Win Health", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            Shutdown(0);
            return;
        }
        await Installer.UninstallAsync(Services.GetRequiredService<SchedulerService>());
        MessageBox.Show("KL Win Health was removed.", "Uninstall KL Win Health", MessageBoxButton.OK, MessageBoxImage.Information);
        Log.CloseAndFlush();
        Shutdown(0);
    }
#endif

    /// <summary>Relaunches the app elevated (used from limited mode).</summary>
    public static void RestartAsAdmin()
    {
        var app = (App)Current;
        // Free the single-instance mutex first, otherwise the elevated copy would just signal us and exit.
        try { app._mutex?.ReleaseMutex(); } catch { }
        app._mutex?.Dispose();
        app._mutex = null;
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = true, Verb = "runas" });
            app.ExitApp();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Restart as admin cancelled/failed");
            app._mutex = new Mutex(true, MutexName, out _);
        }
    }
}
