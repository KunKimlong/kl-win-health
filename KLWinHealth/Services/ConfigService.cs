using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace KLWinHealth.Services;

public enum RuleCategory { Protected, Safe, Caution, Unknown }

public class ServiceRule
{
    /// <summary>Service-name patterns (wildcards * and ? allowed, case-insensitive).</summary>
    public List<string> Names { get; set; } = new();
    /// <summary>Optional display-name patterns.</summary>
    public List<string> DisplayNames { get; set; } = new();
    public RuleCategory Category { get; set; }
    /// <summary>Short label shown in the list (e.g. "Fan & performance modes").</summary>
    public string Label { get; set; } = "";
    /// <summary>Why it is protected / safe / caution.</summary>
    public string Reason { get; set; } = "";

    public bool Matches(string name, string displayName) =>
        Names.Any(p => Wildcard.IsMatch(name, p)) || DisplayNames.Any(p => Wildcard.IsMatch(displayName, p));
}

public class ServiceRuleSet
{
    public int Version { get; set; }
    public string? Notes { get; set; }
    /// <summary>ROG module only: service/display-name patterns that identify a service as ASUS/ROG.</summary>
    public List<string> DetectPatterns { get; set; } = new();
    /// <summary>ROG module only: publisher (file CompanyName) substrings that identify ASUS software.</summary>
    public List<string> DetectPublishers { get; set; } = new();
    public List<ServiceRule> Rules { get; set; } = new();

    public ServiceRule? Find(string name, string displayName) => Rules.FirstOrDefault(r => r.Matches(name, displayName));
}

public class CleanupCategoryDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Level { get; set; } = "Safe"; // Safe | Caution
    /// <summary>Folder | RecycleBin | WindowsUpdate | WindowsOld | Dism | BrowserShortcut</summary>
    public string Kind { get; set; } = "Folder";
    public List<string> Paths { get; set; } = new();
    public List<string> FilePatterns { get; set; } = new() { "*" };
    public bool Recursive { get; set; } = true;
    public double MinAgeDays { get; set; }
    public double MinAgeHours { get; set; }
}

public class CleanupCategorySet
{
    public int Version { get; set; }
    public List<CleanupCategoryDef> Categories { get; set; } = new();
}

/// <summary>
/// Loads the JSON lists from %ProgramData%\KLWinHealth\config\ (editable). Defaults are embedded in the exe and
/// extracted on first run; a newer embedded "version" replaces an older file (the old one is kept as .old).
/// </summary>
public class ConfigService
{
    public ServiceRuleSet RogServices { get; private set; } = new();
    public ServiceRuleSet WindowsServices { get; private set; } = new();
    public CleanupCategorySet CleanupCategories { get; private set; } = new();

    public void Load()
    {
        RogServices = LoadFile<ServiceRuleSet>("rog_services.json", s => s.Version);
        WindowsServices = LoadFile<ServiceRuleSet>("windows_services.json", s => s.Version);
        CleanupCategories = LoadFile<CleanupCategorySet>("cleanup_categories.json", s => s.Version);
    }

    private static T LoadFile<T>(string name, Func<T, int> version) where T : new()
    {
        var embedded = ReadEmbedded(name);
        var embeddedObj = embedded == null ? new T() : JsonSerializer.Deserialize<T>(embedded, Json.Options) ?? new T();
        var path = Path.Combine(AppPaths.Config, name);
        try
        {
            Directory.CreateDirectory(AppPaths.Config);
            if (File.Exists(path))
            {
                var fileObj = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json.Options);
                if (fileObj != null && version(fileObj) >= version(embeddedObj)) return fileObj;
                File.Copy(path, path + ".old", true);
                Log.Information("Config {Name} upgraded to embedded version {Version}", name, version(embeddedObj));
            }
            if (embedded != null) File.WriteAllText(path, embedded);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load config {Name} from disk; using built-in defaults", name);
        }
        return embeddedObj;
    }

    private static string? ReadEmbedded(string name)
    {
        using var s = typeof(ConfigService).Assembly.GetManifestResourceStream("KLWinHealth.Config." + name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

public static class Wildcard
{
    private static readonly Dictionary<string, Regex> Cache = new();

    public static bool IsMatch(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
        Regex? rx;
        lock (Cache)
        {
            if (!Cache.TryGetValue(pattern, out rx))
            {
                rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Cache[pattern] = rx;
            }
        }
        return rx.IsMatch(input);
    }
}
