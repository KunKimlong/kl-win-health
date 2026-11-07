using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>
/// Desktop wallpaper (with backup of the original), history, slideshow (runs while the app is in the tray)
/// and lock screen image via the PersonalizationCSP policy key (with backup).
/// </summary>
public class WallpaperService
{
    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".jfif" };
    public const string ImageFilter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.jfif";
    private const int HistoryMax = 12;

    private const string CspKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP";

    private readonly SettingsService _settings;
    private readonly BackupService _backups;
    private readonly DispatcherTimer _timer = new();
    private readonly Random _rng = new();
    private List<string> _queue = new();

    public event Action? Changed;

    public WallpaperService(SettingsService settings, BackupService backups)
    {
        _settings = settings;
        _backups = backups;
        _timer.Tick += (_, _) => { _ = NextAsync(); };
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ------------------------------------------------------------------ desktop
    /// <summary>Backs up the ORIGINAL wallpaper once (until it's restored), then sets the new one.</summary>
    public void SetWallpaper(string path, WallpaperFit fit, bool addToHistory = true)
    {
        if (!_backups.HasActiveBackups(Modules.Wallpaper))
            _backups.Create(Modules.Wallpaper, "Original desktop wallpaper", new[] { WallpaperNative.Snapshot() });
        WallpaperNative.Set(path, fit);
        _settings.Current.WallpaperFit = fit.ToString();
        if (addToHistory)
        {
            var h = _settings.Current.WallpaperHistory;
            h.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            h.Insert(0, path);
            if (h.Count > HistoryMax) h.RemoveRange(HistoryMax, h.Count - HistoryMax);
        }
        _settings.Save();
        Changed?.Invoke();
    }

    public async Task<RestoreResult> RestoreOriginalAsync(CancellationToken ct = default)
    {
        StopSlideshow(save: true);
        var r = await _backups.RestoreModuleAsync(Modules.Wallpaper, ct);
        Changed?.Invoke();
        return r;
    }

    public WallpaperFit CurrentFit => Enum.TryParse<WallpaperFit>(_settings.Current.WallpaperFit, out var f) ? f : WallpaperFit.Fill;

    // ------------------------------------------------------------------ slideshow
    public bool IsSlideshowRunning => _timer.IsEnabled;
    public bool IsSlideshowConfigured => !string.IsNullOrEmpty(_settings.Current.Slideshow.Folder) && Directory.Exists(_settings.Current.Slideshow.Folder);

    public static List<string> ImagesIn(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false })
                .Where(IsImage).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Can't list images in {Folder}", folder);
            return new List<string>();
        }
    }

    /// <summary>Called at app start: resumes the slideshow if it was on.</summary>
    public void ResumeIfEnabled()
    {
        var s = _settings.Current.Slideshow;
        if (s.Enabled && IsSlideshowConfigured) StartSlideshow(changeNow: false);
        else Changed?.Invoke();
    }

    public void StartSlideshow(bool changeNow = true)
    {
        var s = _settings.Current.Slideshow;
        if (!IsSlideshowConfigured) throw new InvalidOperationException("Pick a slideshow folder first.");
        if (ImagesIn(s.Folder!).Count == 0) throw new InvalidOperationException("The slideshow folder has no images.");
        s.Enabled = true;
        _settings.Save();
        _queue.Clear();
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(s.IntervalMinutes, 1, 24 * 60));
        _timer.Start();
        Log.Information("Slideshow started: {Folder} every {Min} min (shuffle={Shuffle})", s.Folder, s.IntervalMinutes, s.Shuffle);
        if (changeNow) _ = NextAsync();
        Changed?.Invoke();
    }

    public void StopSlideshow(bool save = true)
    {
        _timer.Stop();
        if (save && _settings.Current.Slideshow.Enabled)
        {
            _settings.Current.Slideshow.Enabled = false;
            _settings.Save();
            Log.Information("Slideshow stopped");
        }
        Changed?.Invoke();
    }

    public void ToggleSlideshow()
    {
        if (IsSlideshowRunning) StopSlideshow();
        else StartSlideshow();
    }

    /// <summary>Shows the next slideshow image (also used by the tray "Next wallpaper").</summary>
    public async Task NextAsync()
    {
        var s = _settings.Current.Slideshow;
        if (!IsSlideshowConfigured) return;
        try
        {
            var images = await Task.Run(() => ImagesIn(s.Folder!));
            if (images.Count == 0) return;
            string next;
            if (s.Shuffle)
            {
                _queue.RemoveAll(p => !images.Contains(p));
                if (_queue.Count == 0) _queue = images.OrderBy(_ => _rng.Next()).Where(p => p != s.LastImage).ToList();
                if (_queue.Count == 0) _queue = images.ToList();
                next = _queue[0];
                _queue.RemoveAt(0);
            }
            else
            {
                var idx = s.LastImage == null ? -1 : images.FindIndex(p => string.Equals(p, s.LastImage, StringComparison.OrdinalIgnoreCase));
                next = images[(idx + 1) % images.Count];
            }
            s.LastImage = next;
            var fit = CurrentFit;
            await Task.Run(() => SetWallpaper(next, fit, addToHistory: false));
            if (IsSlideshowRunning) { _timer.Stop(); _timer.Start(); } // restart the interval after a manual "next"
        }
        catch (Exception ex) { Log.Warning(ex, "Slideshow: changing wallpaper failed"); }
    }

    // ------------------------------------------------------------------ lock screen
    public static string? GetLockScreenImage() =>
        RegistryHelper.Read("HKLM", CspKey, "LockScreenImagePath") as string;

    /// <summary>
    /// Copies the image to %ProgramData%\KLWinHealth\lockscreen (readable by the sign-in screen) and sets the
    /// PersonalizationCSP values. The original values are backed up first.
    /// </summary>
    public void SetLockScreen(string imagePath)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found", imagePath);
        Directory.CreateDirectory(AppPaths.LockScreen);
        var dest = Path.Combine(AppPaths.LockScreen, $"lockscreen_{DateTime.Now:yyyyMMdd_HHmmss}{Path.GetExtension(imagePath).ToLowerInvariant()}");
        File.Copy(imagePath, dest, true);

        if (!_backups.HasActiveBackups(Modules.LockScreen))
            _backups.Create(Modules.LockScreen, "Original lock screen settings", new[]
            {
                RegistryHelper.Snapshot("HKLM", CspKey, "LockScreenImagePath"),
                RegistryHelper.Snapshot("HKLM", CspKey, "LockScreenImageUrl"),
                RegistryHelper.Snapshot("HKLM", CspKey, "LockScreenImageStatus")
            });
        RegistryHelper.Write("HKLM", CspKey, "LockScreenImagePath", dest, RegistryValueKind.String);
        RegistryHelper.Write("HKLM", CspKey, "LockScreenImageUrl", dest, RegistryValueKind.String);
        RegistryHelper.Write("HKLM", CspKey, "LockScreenImageStatus", 1, RegistryValueKind.DWord);
        Log.Information("Lock screen image set to {Path}", dest);

        // Remove older copies we made (keep the current one).
        foreach (var old in Directory.EnumerateFiles(AppPaths.LockScreen).Where(f => !string.Equals(f, dest, StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(old); } catch { }
        }
        Changed?.Invoke();
    }

    public async Task<RestoreResult> RestoreLockScreenAsync(CancellationToken ct = default)
    {
        var r = await _backups.RestoreModuleAsync(Modules.LockScreen, ct);
        Changed?.Invoke();
        return r;
    }
}
