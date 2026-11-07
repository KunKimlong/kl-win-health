using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using KLWinHealth.Models;
using Microsoft.Win32;
using Serilog;

namespace KLWinHealth.Services;

public enum WallpaperFit { Fill, Fit, Stretch, Center, Tile, Span }

/// <summary>Sets the desktop wallpaper via IDesktopWallpaper (COM), falling back to SystemParametersInfo.</summary>
public static class WallpaperNative
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        Native.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        void SetBackgroundColor(uint color);
        uint GetBackgroundColor();
        void SetPosition(int position);
        int GetPosition();
        void SetSlideshow(IntPtr items);
        IntPtr GetSlideshow();
        void SetSlideshowOptions(int options, uint slideshowTick);
        void GetSlideshowOptions(out int options, out uint slideshowTick);
        void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, int direction);
        int GetStatus();
        void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
    }

    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");

    // DESKTOP_WALLPAPER_POSITION
    private static int ToComPosition(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Center => 0, WallpaperFit.Tile => 1, WallpaperFit.Stretch => 2,
        WallpaperFit.Fit => 3, WallpaperFit.Fill => 4, WallpaperFit.Span => 5, _ => 4
    };

    private static (string style, string tile) ToRegistry(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fill => ("10", "0"), WallpaperFit.Fit => ("6", "0"), WallpaperFit.Stretch => ("2", "0"),
        WallpaperFit.Center => ("0", "0"), WallpaperFit.Tile => ("0", "1"), WallpaperFit.Span => ("22", "0"),
        _ => ("10", "0")
    };

    private const string DesktopKey = @"Control Panel\Desktop";

    public static string? GetCurrentPath()
    {
        var sb = new StringBuilder(1024);
        return Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) ? sb.ToString() : null;
    }

    public static string TranscodedWallpaperPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

    /// <summary>Snapshot of the current wallpaper for a backup (also copies the transcoded image as a fallback).</summary>
    public static BackupEntry Snapshot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
        var e = new BackupEntry
        {
            Kind = BackupKind.Wallpaper,
            WallpaperPath = GetCurrentPath(),
            WallpaperStyle = key?.GetValue("WallpaperStyle") as string,
            TileWallpaper = key?.GetValue("TileWallpaper") as string
        };
        try
        {
            if (File.Exists(TranscodedWallpaperPath))
            {
                var copy = Path.Combine(AppPaths.Backups, $"wallpaper_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg");
                File.Copy(TranscodedWallpaperPath, copy, true);
                e.WallpaperCopy = copy;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Could not copy the current wallpaper for backup"); }
        return e;
    }

    public static void Set(string imagePath, WallpaperFit fit)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException("Image not found", imagePath);
        var (style, tile) = ToRegistry(fit);
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey, true))
        {
            key.SetValue("WallpaperStyle", style, RegistryValueKind.String);
            key.SetValue("TileWallpaper", tile, RegistryValueKind.String);
        }

        try
        {
            var type = Type.GetTypeFromCLSID(ClsidDesktopWallpaper, true)!;
            var dw = (IDesktopWallpaper)Activator.CreateInstance(type)!;
            try
            {
                dw.SetPosition(ToComPosition(fit));
                dw.SetWallpaper(null, imagePath);
            }
            finally { Marshal.ReleaseComObject(dw); }
            Log.Information("Wallpaper set via IDesktopWallpaper: {Path} ({Fit})", imagePath, fit);
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "IDesktopWallpaper failed, falling back to SystemParametersInfo");
        }

        if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, imagePath, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows refused to set the wallpaper.");
        Log.Information("Wallpaper set via SystemParametersInfo: {Path} ({Fit})", imagePath, fit);
    }

    public static void RestoreFromBackup(BackupEntry e)
    {
        var path = e.WallpaperPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = e.WallpaperCopy;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            // Original was a solid color (empty path) — clear the image.
            Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, "", Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
            return;
        }
        using (var key = Registry.CurrentUser.CreateSubKey(DesktopKey, true))
        {
            if (e.WallpaperStyle != null) key.SetValue("WallpaperStyle", e.WallpaperStyle, RegistryValueKind.String);
            if (e.TileWallpaper != null) key.SetValue("TileWallpaper", e.TileWallpaper, RegistryValueKind.String);
        }
        if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Windows refused to restore the wallpaper.");
    }
}
