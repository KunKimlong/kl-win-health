using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KLWinHealth.Models;
using Serilog;

namespace KLWinHealth.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Loads/saves settings.json; protects the VirusTotal key with DPAPI.</summary>
public class SettingsService
{
    private readonly object _lock = new();
    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Saved;

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Json.Options) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read settings.json; using defaults (the broken file is kept as settings.bad.json)");
            try { File.Copy(AppPaths.SettingsFile, Path.Combine(AppPaths.Root, "settings.bad.json"), true); } catch { }
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock) json = JsonSerializer.Serialize(Current, Json.Options);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.SettingsFile, true);
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save settings.json");
        }
    }

    // ------------------------------------------------------------ VirusTotal key (DPAPI)
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KLWinHealth.VirusTotal.v1");

    public string? GetVirusTotalKey()
    {
        var p = Current.VirusTotalApiKeyProtected;
        if (string.IsNullOrWhiteSpace(p)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(p), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not decrypt the VirusTotal API key");
            return null;
        }
    }

    public void SetVirusTotalKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            Current.VirusTotalApiKeyProtected = null;
        }
        else
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
            Current.VirusTotalApiKeyProtected = Convert.ToBase64String(bytes);
        }
        Save();
    }
}
