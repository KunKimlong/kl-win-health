using System.IO;

namespace KLWinHealth.Services;

/// <summary>Locations under %ProgramData%\KLWinHealth\.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KLWinHealth");

    public static string Backups => Path.Combine(Root, "backups");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Quarantine => Path.Combine(Root, "quarantine");
    public static string Config => Path.Combine(Root, "config");
    public static string LockScreen => Path.Combine(Root, "lockscreen");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Path of the running .exe (works for single-file publish and dotnet run).</summary>
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "KLWinHealth.exe");

    public static void EnsureCreated()
    {
        foreach (var d in new[] { Root, Backups, Logs, Quarantine, Config, LockScreen })
        {
            try { Directory.CreateDirectory(d); } catch { /* reported by caller when used */ }
        }
    }
}
