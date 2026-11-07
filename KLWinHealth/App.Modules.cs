using KLWinHealth.Services;
using KLWinHealth.Services.VirusScan;
using KLWinHealth.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace KLWinHealth;

/// <summary>Module registrations and background features (kept apart from the startup plumbing in App.xaml.cs).</summary>
public partial class App
{
    static partial void RegisterModules(IServiceCollection s)
    {
        // Phase 2 — Virus check
        s.AddSingleton<DefenderScanner>();
        s.AddSingleton<VirusTotalClient>();
        s.AddSingleton<QuarantineService>();
        s.AddSingleton<VirusCheckViewModel>();

        // Phases 3 + 4 — Clean ROG service / Clean Windows service
        s.AddSingleton<ServiceManager>();
        s.AddSingleton<RogServicesViewModel>();
        s.AddSingleton<WindowsServicesViewModel>();

        // Phase 5 — Browser cleaner
        s.AddSingleton<BrowserService>();
        s.AddSingleton<BrowserCleanerViewModel>();

        // Phase 6 — Clean storage (StorageService is also used by the headless --autoclean run)
        s.AddSingleton<StorageService>();
        s.AddSingleton<StorageViewModel>();

        // Phase 7 — Wallpaper
        s.AddSingleton<WallpaperService>();
        s.AddSingleton<WallpaperViewModel>();

        // Phase 8 — Desktop widgets
        s.AddSingleton<KLWinHealth.Services.Widgets.WidgetManager>();
        s.AddSingleton<WidgetsViewModel>();
    }

    partial void StartBackgroundFeaturesImpl()
    {
        // Files dropped while on another page → open Virus check and scan them.
        if (MainWindow is MainWindow mw)
        {
            mw.UnhandledFilesDropped += files =>
            {
                var nav = Services.GetRequiredService<NavigationService>();
                var virus = Services.GetRequiredService<VirusCheckViewModel>();
                nav.NavigateTo<VirusCheckViewModel>();
                _ = virus.ScanPathsAsync(files);
            };
        }

        // Wallpaper slideshow keeps running from the tray; tray items "Next wallpaper" / "Wallpaper slideshow".
        var tray = Services.GetRequiredService<TrayService>();
        var wallpaper = Services.GetRequiredService<WallpaperService>();
        void SyncTray() => Dispatcher.BeginInvoke(() => tray.SetSlideshowState(wallpaper.IsSlideshowConfigured, wallpaper.IsSlideshowRunning));
        wallpaper.Changed += SyncTray;
        tray.NextWallpaperRequested += () => _ = wallpaper.NextAsync();
        tray.SlideshowToggleRequested += () =>
        {
            try { wallpaper.ToggleSlideshow(); }
            catch (Exception ex) { tray.ShowNotification("Wallpaper slideshow", ex.Message); }
        };
        Services.GetRequiredService<SettingsService>().Saved += (_, _) => SyncTray();
        wallpaper.ResumeIfEnabled();

        // Desktop widgets: tray "Widgets: edit mode / locked", widget menu "Edit in KL Win Health".
        var widgets = Services.GetRequiredService<KLWinHealth.Services.Widgets.WidgetManager>();
        tray.WidgetsLockRequested += locked => widgets.SetLocked(locked);
        widgets.LockedChanged += locked => tray.SetWidgetsLocked(locked);
        widgets.EditRequested += config =>
        {
            ShowMainWindow();
            Services.GetRequiredService<NavigationService>().NavigateTo<WidgetsViewModel>();
            Services.GetRequiredService<WidgetsViewModel>().Select(config);
        };
#if KL_DEV_NOELEVATE
        // Dev snapshots must not put windows on the developer's real desktop.
        if (Environment.GetCommandLineArgs().Any(a => a.StartsWith("--devshot=", StringComparison.OrdinalIgnoreCase)))
        {
            widgets.DevPreview();
            return;
        }
        var devWidgetTest = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--devwidgettest=", StringComparison.OrdinalIgnoreCase))?[16..];
        if (devWidgetTest != null)
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { System.IO.File.WriteAllText(devWidgetTest, await widgets.DevWindowTestAsync()); }
                catch (Exception ex) { System.IO.File.WriteAllText(devWidgetTest, ex.ToString()); }
                ExitApp();
            });
            return;
        }
#endif
        widgets.Start();
    }

    partial void StopBackgroundFeaturesImpl()
    {
        Services.GetService<WallpaperService>()?.StopSlideshow(save: false);
        Services.GetService<KLWinHealth.Services.Widgets.WidgetManager>()?.Stop();
    }
}
