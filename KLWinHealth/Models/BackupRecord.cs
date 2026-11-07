using System.Text.Json.Serialization;

namespace KLWinHealth.Models;

public enum BackupKind { Service, Registry, Wallpaper }

/// <summary>One backup file: %ProgramData%\KLWinHealth\backups\&lt;timestamp&gt;_&lt;module&gt;.json</summary>
public class BackupRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Module { get; set; } = "";
    public string Description { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = new();

    [JsonIgnore] public string FilePath { get; set; } = "";
    [JsonIgnore] public bool IsFullyRestored => Entries.Count > 0 && Entries.All(e => e.Restored);
    [JsonIgnore] public string StatusText => IsFullyRestored ? "Restored" : Entries.Any(e => e.Restored) ? "Partly restored" : "Active";
    [JsonIgnore] public string DateText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>The original state of one thing (service, registry value or wallpaper) before the app changed it.</summary>
public class BackupEntry
{
    public BackupKind Kind { get; set; }
    public bool Restored { get; set; }
    public DateTime? RestoredAt { get; set; }

    // Service
    public string? ServiceName { get; set; }
    public string? ServiceDisplayName { get; set; }
    public string? StartupType { get; set; }
    public bool WasRunning { get; set; }

    // Registry
    public string? Hive { get; set; }          // "HKLM" or "HKCU"
    public string? KeyPath { get; set; }
    public string? ValueName { get; set; }
    public bool KeyExisted { get; set; }
    public bool ValueExisted { get; set; }
    public string? ValueKind { get; set; }      // RegistryValueKind name
    public string? ValueData { get; set; }      // invariant string form

    // Wallpaper
    public string? WallpaperPath { get; set; }
    public string? WallpaperStyle { get; set; }
    public string? TileWallpaper { get; set; }
    public string? WallpaperCopy { get; set; }  // copy of the transcoded wallpaper as a fallback

    /// <summary>Key identifying the restored "thing" — entries with the same target are the same item.</summary>
    [JsonIgnore]
    public string Target => Kind switch
    {
        BackupKind.Service => "svc:" + ServiceName?.ToLowerInvariant(),
        BackupKind.Registry => $"reg:{Hive}\\{KeyPath}\\{ValueName}".ToLowerInvariant(),
        BackupKind.Wallpaper => "wallpaper",
        _ => Guid.NewGuid().ToString()
    };

    [JsonIgnore]
    public string Display => Kind switch
    {
        BackupKind.Service => $"Service {ServiceDisplayName ?? ServiceName}: start type {StartupType}, {(WasRunning ? "running" : "stopped")}",
        BackupKind.Registry => ValueExisted
            ? $"{Hive}\\{KeyPath}\\{ValueName} = {ValueData}"
            : $"{Hive}\\{KeyPath}\\{ValueName} (did not exist)",
        BackupKind.Wallpaper => $"Wallpaper: {WallpaperPath}",
        _ => ""
    };
}
