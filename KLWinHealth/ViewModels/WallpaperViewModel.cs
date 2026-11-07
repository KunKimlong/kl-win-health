using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Services;
using Serilog;

namespace KLWinHealth.ViewModels;

/// <summary>A small thumbnail of an image file (decoded off the UI thread, file not kept open).</summary>
public class WallpaperThumb
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public ImageSource? Image { get; init; }

    public static ImageSource? Load(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;      // read fully, then close the file
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Thumbnail failed for {Path}", path);
            return null;
        }
    }
}

/// <summary>Module 6 — Wallpaper: pick, preview, fit, apply, slideshow, lock screen, history, restore.</summary>
public partial class WallpaperViewModel : PageViewModel, IFileDropTarget
{
    private readonly WallpaperService _wallpaper;

    public override string Title => "Wallpaper";
    public override string Subtitle => "Pick an image, preview how it fits and set it on the desktop or lock screen. Your original wallpaper is backed up.";

    public WallpaperFit[] FitModes { get; } = Enum.GetValues<WallpaperFit>();
    public record IntervalOption(int Minutes, string Text);
    public IntervalOption[] IntervalOptions { get; } = new[] { 1, 5, 10, 15, 30, 60, 120, 180, 360, 720, 1440 }.Select(m => new IntervalOption(m, Format(m))).ToArray();
    public ObservableCollection<WallpaperThumb> History { get; } = new();

    [ObservableProperty] private string? _selectedImage;
    [ObservableProperty] private string _selectedInfo = "";
    [ObservableProperty] private WallpaperFit _fit;
    [ObservableProperty] private Brush? _previewBrush;
    [ObservableProperty] private string? _currentWallpaper;
    [ObservableProperty] private string? _currentLockScreen;

    [ObservableProperty] private string? _slideshowFolder;
    [ObservableProperty] private int _slideshowInterval = 30;
    [ObservableProperty] private bool _slideshowShuffle = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SlideshowStateText))] private bool _isSlideshowRunning;
    public string SlideshowStateText => IsSlideshowRunning ? "Running" : "Stopped";
    [ObservableProperty] private string _slideshowInfo = "";

    private ImageSource? _previewImage;
    private (int w, int h) _imagePixels;

    /// <summary>Preview canvas = primary screen size in physical pixels (the Viewbox scales it down).</summary>
    public double ScreenWidth { get; } = Math.Max(640, Native.GetSystemMetrics(0));
    public double ScreenHeight { get; } = Math.Max(480, Native.GetSystemMetrics(1));

    public WallpaperViewModel(PageContext ctx, WallpaperService wallpaper) : base(ctx)
    {
        _wallpaper = wallpaper;
        _wallpaper.Changed += () => Application.Current?.Dispatcher.BeginInvoke(RefreshState);
        _fit = _wallpaper.CurrentFit;
        var s = Settings.Current.Slideshow;
        _slideshowFolder = s.Folder;
        _slideshowInterval = s.IntervalMinutes;
        _slideshowShuffle = s.Shuffle;
    }

    public override async Task OnNavigatedToAsync()
    {
        RefreshState();
        await LoadHistoryAsync();
        if (SelectedImage == null)
        {
            var cur = CurrentWallpaper is { } c && File.Exists(c) ? c :
                      File.Exists(WallpaperNative.TranscodedWallpaperPath) ? WallpaperNative.TranscodedWallpaperPath : null;
            if (cur != null) await SelectAsync(cur);
        }
    }

    private void RefreshState()
    {
        CurrentWallpaper = WallpaperNative.GetCurrentPath();
        CurrentLockScreen = WallpaperService.GetLockScreenImage();
        IsSlideshowRunning = _wallpaper.IsSlideshowRunning;
        var count = !string.IsNullOrEmpty(SlideshowFolder) && Directory.Exists(SlideshowFolder) ? WallpaperService.ImagesIn(SlideshowFolder).Count : 0;
        SlideshowInfo = string.IsNullOrEmpty(SlideshowFolder) ? "No folder selected."
            : count == 0 ? "No images in this folder."
            : IsSlideshowRunning ? $"Running — {count} images, every {Format(SlideshowInterval)}. Keeps running from the tray."
            : $"{count} images. Stopped.";
    }

    private static string Format(int minutes) => minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes} min";

    private async Task LoadHistoryAsync()
    {
        var paths = Settings.Current.WallpaperHistory.Where(File.Exists).ToList();
        var thumbs = await Task.Run(() => paths.Select(p => new WallpaperThumb { Path = p, Image = WallpaperThumb.Load(p, 200) }).ToList());
        History.Clear();
        foreach (var t in thumbs) History.Add(t);
    }

    // ------------------------------------------------------------------ select / preview
    public void OnFilesDropped(string[] paths)
    {
        var img = paths.FirstOrDefault(WallpaperService.IsImage);
        if (img != null) _ = SelectAsync(img);
        else if (paths.FirstOrDefault(Directory.Exists) is { } dir) SlideshowFolder = dir;
        else Dialogs.Toast("Drop an image file (JPG, PNG, BMP…) or a folder for the slideshow.", ToastKind.Warning);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var file = Dialogs.PickFile("Choose a wallpaper", WallpaperService.ImageFilter);
        if (file != null) await SelectAsync(file);
    }

    [RelayCommand]
    private Task SelectHistoryAsync(WallpaperThumb? t) => t == null ? Task.CompletedTask : SelectAsync(t.Path);

    private async Task SelectAsync(string path)
    {
        try
        {
            var (img, w, h) = await Task.Run(() =>
            {
                var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                var pw = frame.PixelWidth;
                var ph = frame.PixelHeight;
                return (WallpaperThumb.Load(path, Math.Min(pw, 1600)), pw, ph);
            });
            if (img == null) throw new InvalidDataException("This image can't be read.");
            _previewImage = img;
            _imagePixels = (w, h);
            SelectedImage = path;
            var size = new FileInfo(path).Length;
            SelectedInfo = $"{w} × {h} · {Converters.Format.Bytes(size)}" +
                           (w < ScreenWidth || h < ScreenHeight ? $" · smaller than your screen ({ScreenWidth:0} × {ScreenHeight:0})" : "");
            UpdatePreview();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Can't open image {Path}", path);
            Dialogs.Toast("Couldn't open that image: " + FriendlyError(ex), ToastKind.Warning);
        }
    }

    partial void OnFitChanged(WallpaperFit value) => UpdatePreview();

    /// <summary>Builds a brush that mimics how Windows draws the wallpaper with the selected fit mode.</summary>
    private void UpdatePreview()
    {
        if (_previewImage == null) { PreviewBrush = null; return; }
        double W = ScreenWidth, H = ScreenHeight, w = _imagePixels.w, h = _imagePixels.h;
        var b = new ImageBrush(_previewImage) { AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        switch (Fit)
        {
            case WallpaperFit.Fill:
            case WallpaperFit.Span:
                b.Stretch = Stretch.UniformToFill; break;
            case WallpaperFit.Fit:
                b.Stretch = Stretch.Uniform; break;
            case WallpaperFit.Stretch:
                b.Stretch = Stretch.Fill; break;
            case WallpaperFit.Center:
                b.Stretch = Stretch.Fill;
                b.ViewportUnits = BrushMappingMode.Absolute;
                b.Viewport = new Rect((W - w) / 2, (H - h) / 2, w, h);
                break;
            case WallpaperFit.Tile:
                b.Stretch = Stretch.Fill;
                b.TileMode = TileMode.Tile;
                b.ViewportUnits = BrushMappingMode.Absolute;
                b.Viewport = new Rect(0, 0, w, h);
                break;
        }
        b.Freeze();
        PreviewBrush = b;
    }

    // ------------------------------------------------------------------ apply
    [RelayCommand]
    private async Task ApplyDesktopAsync()
    {
        var path = SelectedImage;
        if (path == null) { Dialogs.Toast("Choose an image first."); return; }
        if (string.Equals(path, WallpaperNative.TranscodedWallpaperPath, StringComparison.OrdinalIgnoreCase))
        {
            Dialogs.Toast("That's already your current wallpaper.");
            return;
        }
        if (IsSlideshowRunning &&
            !await Dialogs.ConfirmAsync("Stop the slideshow?", "The slideshow is running and would replace this wallpaper. Stop it and set this image?", null, "Stop and set"))
            return;
        if (IsSlideshowRunning) _wallpaper.StopSlideshow();
        var fit = Fit;
        var ok = await RunBusyAsync("Setting wallpaper…", _ => Task.Run(() => _wallpaper.SetWallpaper(path, fit)), cancellable: false);
        if (ok)
        {
            Dialogs.Toast("Wallpaper set.", ToastKind.Success);
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task ApplyLockScreenAsync()
    {
        var path = SelectedImage;
        if (path == null) { Dialogs.Toast("Choose an image first."); return; }
        if (!App.IsAdmin) { Dialogs.Toast("Setting the lock screen needs administrator rights.", ToastKind.Warning); return; }
        if (!await Dialogs.ConfirmAsync("Set lock screen image?",
                "The image is copied to KL Win Health's data folder and set as the lock screen for all users.",
                new[] { "Press Win+L to see it.", "Your previous lock screen settings are backed up — use \"Restore lock screen\" to go back." },
                "Set lock screen",
                warning: "While this is set, the lock screen picture can't be changed in Windows Settings (it's set by policy).")) return;
        var ok = await RunBusyAsync("Setting lock screen…", _ => Task.Run(() => _wallpaper.SetLockScreen(path)), cancellable: false);
        if (ok) Dialogs.Toast("Lock screen image set. Press Win+L to see it.", ToastKind.Success);
    }

    [RelayCommand]
    private async Task RestoreOriginalAsync()
    {
        if (!Backups.HasActiveBackups(Modules.Wallpaper)) { Dialogs.Toast("Nothing to restore — the wallpaper wasn't changed by KL Win Health."); return; }
        if (!await Dialogs.ConfirmAsync("Restore original wallpaper?", "Puts back the wallpaper you had before KL Win Health changed it. The slideshow is stopped.", null, "Restore")) return;
        RestoreResult? r = null;
        await RunBusyAsync("Restoring wallpaper…", async ct => r = await _wallpaper.RestoreOriginalAsync(ct), cancellable: false);
        if (r != null) Dialogs.Toast(r.Failed == 0 ? "Original wallpaper restored." : r.Summary, r.Failed == 0 ? ToastKind.Success : ToastKind.Error);
        RefreshState();
    }

    [RelayCommand]
    private async Task RestoreLockScreenAsync()
    {
        if (!Backups.HasActiveBackups(Modules.LockScreen)) { Dialogs.Toast("Nothing to restore — the lock screen wasn't changed by KL Win Health."); return; }
        if (!await Dialogs.ConfirmAsync("Restore lock screen?", "Removes the lock screen image set by KL Win Health and puts back the previous settings.", null, "Restore")) return;
        RestoreResult? r = null;
        await RunBusyAsync("Restoring lock screen…", async ct => r = await _wallpaper.RestoreLockScreenAsync(ct), cancellable: false);
        if (r != null) Dialogs.Toast(r.Failed == 0 ? "Lock screen restored." : r.Summary, r.Failed == 0 ? ToastKind.Success : ToastKind.Error);
        RefreshState();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Settings.Current.WallpaperHistory.Clear();
        Settings.Save();
        History.Clear();
    }

    // ------------------------------------------------------------------ slideshow
    [RelayCommand]
    private void BrowseSlideshowFolder()
    {
        var folder = Dialogs.PickFolder("Choose a folder of wallpapers");
        if (folder != null) SlideshowFolder = folder;
    }

    partial void OnSlideshowFolderChanged(string? value) => SaveSlideshowSettings();
    partial void OnSlideshowIntervalChanged(int value) => SaveSlideshowSettings();
    partial void OnSlideshowShuffleChanged(bool value) => SaveSlideshowSettings();

    private void SaveSlideshowSettings()
    {
        var s = Settings.Current.Slideshow;
        var restart = _wallpaper.IsSlideshowRunning && (s.Folder != SlideshowFolder || s.IntervalMinutes != SlideshowInterval);
        s.Folder = SlideshowFolder;
        s.IntervalMinutes = SlideshowInterval;
        s.Shuffle = SlideshowShuffle;
        Settings.Save();
        if (restart)
        {
            try { _wallpaper.StartSlideshow(changeNow: false); }
            catch (Exception ex) { _wallpaper.StopSlideshow(); Dialogs.Toast(ex.Message, ToastKind.Warning); }
        }
        RefreshState();
    }

    [RelayCommand]
    private void ToggleSlideshow()
    {
        try
        {
            _wallpaper.ToggleSlideshow();
            Dialogs.Toast(_wallpaper.IsSlideshowRunning ? "Slideshow started. It keeps running while KL Win Health is in the tray." : "Slideshow stopped.",
                _wallpaper.IsSlideshowRunning ? ToastKind.Success : ToastKind.Info);
        }
        catch (Exception ex) { Dialogs.Toast(ex.Message, ToastKind.Warning); }
        RefreshState();
    }

    [RelayCommand]
    private Task NextWallpaperAsync() => _wallpaper.NextAsync();
}
