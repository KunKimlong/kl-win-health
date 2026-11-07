namespace KLWinHealth.Models;

/// <summary>Everything persisted to %ProgramData%\KLWinHealth\settings.json.</summary>
public class AppSettings
{
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// Start (minimized to tray) when the user signs in, so desktop widgets and the slideshow come back after a restart.
    /// null = the user never chose → treated as on. Set to false only when the user turns it off in Settings.
    /// </summary>
    public bool? StartWithWindows { get; set; }

    /// <summary>DPAPI-protected (CurrentUser scope) VirusTotal key, base64.</summary>
    public string? VirusTotalApiKeyProtected { get; set; }

    public bool AutoCleanEnabled { get; set; }
    public DayOfWeek AutoCleanDay { get; set; } = DayOfWeek.Sunday;
    public string AutoCleanTime { get; set; } = "12:00";

    // Virus check
    public int LastScanThreats { get; set; }
    public DateTime? LastScanTime { get; set; }

    // Wallpaper
    public List<string> WallpaperHistory { get; set; } = new();
    public string WallpaperFit { get; set; } = "Fill";
    public SlideshowSettings Slideshow { get; set; } = new();

    // Widgets
    public bool WidgetsLocked { get; set; } = true;
    public bool WidgetsSnapToGrid { get; set; } = true;
    public int WidgetsGridSize { get; set; } = 10;
    public List<WidgetConfig> Widgets { get; set; } = new();
}

public class SlideshowSettings
{
    public bool Enabled { get; set; }
    public string? Folder { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public bool Shuffle { get; set; } = true;
    public string? LastImage { get; set; }
}
