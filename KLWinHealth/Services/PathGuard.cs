using System.IO;

namespace KLWinHealth.Services;

/// <summary>
/// Last line of defence for anything that deletes files: personal folders, Prefetch, drive roots and
/// core Windows folders are always refused, whatever a config file says.
/// </summary>
public static class PathGuard
{
    private static readonly Lazy<List<string>> NeverInside = new(BuildNeverInside);
    private static readonly Lazy<List<string>> NeverEqual = new(BuildNeverEqual);

    private static List<string> BuildNeverInside()
    {
        var list = new List<string>();
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p)) list.Add(Normalize(p)); }

        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.Favorites, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments,
                     Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonMusic, Environment.SpecialFolder.CommonVideos
                 })
            Add(Environment.GetFolderPath(f));

        Add(Native.GetKnownFolder(Native.FOLDERID_Downloads));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var sub in new[] { "Downloads", "Documents", "Desktop", "Pictures", "Music", "Videos", "OneDrive", "Saved Games", "Contacts", "Links", "Searches", "3D Objects", "Favorites" })
            Add(Path.Combine(profile, sub));
        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add(Path.Combine(win, "Prefetch"));
        Add(Path.Combine(win, "System32"));
        Add(Path.Combine(win, "SysWOW64"));
        Add(Path.Combine(win, "WinSxS"));
        Add(AppPaths.Root);
        return list;
    }

    private static List<string> BuildNeverEqual()
    {
        var list = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users"
        };
        return list.Where(p => !string.IsNullOrEmpty(p)).Select(Normalize).ToList();
    }

    public static string Normalize(string p)
    {
        try { p = Path.GetFullPath(p); } catch { }
        return p.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>True if the folder may be cleaned (it is not personal / critical).</summary>
    public static bool IsCleanableFolder(string folder, out string reason)
    {
        reason = "";
        var n = Normalize(folder);
        if (n.Length <= 3 || Path.GetPathRoot(n)?.TrimEnd('\\').ToLowerInvariant() == n)
        {
            reason = "Refusing to clean a drive root.";
            return false;
        }
        if (NeverEqual.Value.Contains(n))
        {
            reason = "Refusing to clean a core system or profile folder.";
            return false;
        }
        foreach (var p in NeverInside.Value)
        {
            if (n == p || n.StartsWith(p + "\\", StringComparison.Ordinal))
            {
                reason = "This is a personal or protected folder and is never cleaned.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Check for files the USER picked to send to the Recycle Bin (large file finder / Downloads review).
    /// Personal folders are allowed here (it's recoverable and explicitly chosen), but Windows, Program Files,
    /// system-attribute files (pagefile, hiberfil, ...) and the app's own data are refused.
    /// </summary>
    public static bool IsUserRecyclable(string file, out string reason)
    {
        reason = "";
        var n = Normalize(file);
        var blocked = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            AppPaths.Root
        }.Where(p => !string.IsNullOrEmpty(p)).Select(Normalize);
        if (blocked.Any(p => n.StartsWith(p + "\\", StringComparison.Ordinal)))
        {
            reason = "System and program files are never removed here.";
            return false;
        }
        try
        {
            var attr = File.GetAttributes(file);
            if (attr.HasFlag(FileAttributes.System))
            {
                reason = "This is a Windows system file.";
                return false;
            }
        }
        catch (Exception ex) { reason = ex.Message; return false; }
        if (n.Equals(Normalize(AppPaths.ExePath), StringComparison.Ordinal)) { reason = "That's KL Win Health itself."; return false; }
        return true;
    }

    /// <summary>Per-file check used while deleting (defends against junctions/odd paths).</summary>
    public static bool IsDeletableFile(string file)
    {
        var n = Normalize(file);
        foreach (var p in NeverInside.Value)
            if (n.StartsWith(p + "\\", StringComparison.Ordinal)) return false;
        return true;
    }
}
