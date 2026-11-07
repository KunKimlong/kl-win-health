using System.Globalization;
using KLWinHealth.Models;
using Microsoft.Win32;

namespace KLWinHealth.Services;

public static class RegistryHelper
{
    public static RegistryKey Root(string hive) => hive.ToUpperInvariant() switch
    {
        "HKLM" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        "HKCU" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        _ => throw new ArgumentException("Unsupported hive " + hive)
    };

    /// <summary>Captures the current state of a value (for backups).</summary>
    public static BackupEntry Snapshot(string hive, string keyPath, string valueName)
    {
        var e = new BackupEntry { Kind = BackupKind.Registry, Hive = hive, KeyPath = keyPath, ValueName = valueName };
        using var root = Root(hive);
        using var key = root.OpenSubKey(keyPath);
        e.KeyExisted = key != null;
        if (key != null && key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            e.ValueExisted = true;
            var kind = key.GetValueKind(valueName);
            e.ValueKind = kind.ToString();
            e.ValueData = ToInvariant(key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), kind);
        }
        return e;
    }

    public static object? Read(string hive, string keyPath, string valueName)
    {
        using var root = Root(hive);
        using var key = root.OpenSubKey(keyPath);
        return key?.GetValue(valueName);
    }

    public static void Write(string hive, string keyPath, string valueName, object value, RegistryValueKind kind)
    {
        using var root = Root(hive);
        using var key = root.CreateSubKey(keyPath, true);
        key.SetValue(valueName, value, kind);
    }

    /// <summary>Puts a value back exactly as captured in <paramref name="e"/>.</summary>
    public static void Restore(BackupEntry e)
    {
        using var root = Root(e.Hive!);
        if (e.ValueExisted)
        {
            using var key = root.CreateSubKey(e.KeyPath!, true);
            var kind = Enum.Parse<RegistryValueKind>(e.ValueKind ?? "String");
            key.SetValue(e.ValueName!, FromInvariant(e.ValueData, kind), kind);
            return;
        }

        using (var key = root.OpenSubKey(e.KeyPath!, true))
            key?.DeleteValue(e.ValueName!, false);

        if (!e.KeyExisted)
        {
            // Remove the key (and now-empty parents we created, e.g. Policies\Google\Chrome) if empty.
            var path = e.KeyPath!;
            while (!string.IsNullOrEmpty(path) && path.Count(c => c == '\\') >= 2)
            {
                using (var k = root.OpenSubKey(path))
                {
                    if (k == null) { path = Parent(path); continue; }
                    if (k.ValueCount > 0 || k.SubKeyCount > 0) break;
                }
                try { root.DeleteSubKey(path, false); } catch { break; }
                path = Parent(path);
                if (path.EndsWith(@"SOFTWARE\Policies", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
    }

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i < 0 ? "" : path[..i];
    }

    private static string? ToInvariant(object? v, RegistryValueKind kind) => v switch
    {
        null => null,
        string[] arr => string.Join("\n", arr),
        byte[] bytes => Convert.ToBase64String(bytes),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)
    };

    private static object FromInvariant(string? s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)long.Parse(s ?? "0", CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => long.Parse(s ?? "0", CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => (s ?? "").Split('\n'),
        RegistryValueKind.Binary => Convert.FromBase64String(s ?? ""),
        _ => s ?? ""
    };
}
