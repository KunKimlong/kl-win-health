using System.IO;
using System.Text.Json;
using KLWinHealth.Models;
using Serilog;

namespace KLWinHealth.Services;

public static class Modules
{
    public const string RogServices = "RogServices";
    public const string WindowsServices = "WindowsServices";
    public const string Browser = "Browser";
    public const string Wallpaper = "Wallpaper";
    public const string LockScreen = "LockScreen";
}

public record RestoreResult(int Restored, int Failed, List<string> Errors)
{
    public string Summary => Failed == 0
        ? $"{Restored} item(s) restored."
        : $"{Restored} item(s) restored, {Failed} failed:\n• " + string.Join("\n• ", Errors);
}

/// <summary>
/// Saves the original state of every service / registry value / wallpaper before the app changes it,
/// and restores it on request. Files: backups\&lt;yyyyMMdd_HHmmss_fff&gt;_&lt;module&gt;.json
/// </summary>
public class BackupService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? Changed;

    /// <summary>Writes a backup file. Throws if it can't be written — callers must not change anything then.</summary>
    public BackupRecord Create(string module, string description, IEnumerable<BackupEntry> entries)
    {
        var record = new BackupRecord { Module = module, Description = description, Entries = entries.ToList() };
        Directory.CreateDirectory(AppPaths.Backups);
        var name = $"{record.CreatedAt:yyyyMMdd_HHmmss_fff}_{module}.json";
        record.FilePath = Path.Combine(AppPaths.Backups, name);
        File.WriteAllText(record.FilePath, JsonSerializer.Serialize(record, Json.Options));
        Log.Information("Backup created {File}: {Description} ({Count} entries)", name, description, record.Entries.Count);
        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    public List<BackupRecord> LoadAll()
    {
        var list = new List<BackupRecord>();
        if (!Directory.Exists(AppPaths.Backups)) return list;
        foreach (var file in Directory.EnumerateFiles(AppPaths.Backups, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(file), Json.Options);
                if (r == null) continue;
                r.FilePath = file;
                list.Add(r);
            }
            catch (Exception ex) { Log.Warning(ex, "Unreadable backup file {File}", file); }
        }
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public bool HasActiveBackups(string module) =>
        LoadAll().Any(r => r.Module == module && r.Entries.Any(e => !e.Restored));

    /// <summary>Oldest un-restored service snapshot for a service (= its original state).</summary>
    public BackupEntry? FindOriginalService(string serviceName) =>
        LoadAll().SelectMany(r => r.Entries)
            .Where(e => e.Kind == BackupKind.Service && !e.Restored &&
                        string.Equals(e.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
            .LastOrDefault(); // LoadAll is newest-first, so last = oldest

    /// <summary>Restores every un-restored item of a module (or all modules) to its ORIGINAL state.</summary>
    public Task<RestoreResult> RestoreModuleAsync(string? module, CancellationToken ct = default) =>
        RestoreWhereAsync(r => module == null || r.Module == module, null, ct);

    /// <summary>Restores only the entries of one backup file to the state recorded in that file.</summary>
    public Task<RestoreResult> RestoreRecordAsync(BackupRecord record, CancellationToken ct = default) =>
        RestoreWhereAsync(r => r.Id == record.Id, null, ct);

    /// <summary>Restores one service to its original state.</summary>
    public Task<RestoreResult> RestoreServiceAsync(string serviceName, CancellationToken ct = default) =>
        RestoreWhereAsync(_ => true, e => e.Kind == BackupKind.Service &&
                                          string.Equals(e.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase), ct);

    /// <summary>Restores one registry value to its original state.</summary>
    public Task<RestoreResult> RestoreRegistryValueAsync(string hive, string keyPath, string valueName, CancellationToken ct = default) =>
        RestoreWhereAsync(_ => true, e => e.Kind == BackupKind.Registry &&
                                          string.Equals(e.Hive, hive, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(e.KeyPath, keyPath, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(e.ValueName, valueName, StringComparison.OrdinalIgnoreCase), ct);

    private async Task<RestoreResult> RestoreWhereAsync(Func<BackupRecord, bool> recordFilter, Func<BackupEntry, bool>? entryFilter, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var records = LoadAll().Where(recordFilter).ToList(); // newest first
            var pending = records
                .SelectMany(r => r.Entries.Where(e => !e.Restored && (entryFilter == null || entryFilter(e))).Select(e => (r, e)))
                .ToList();

            int ok = 0, failed = 0;
            var errors = new List<string>();

            // Group by target; the OLDEST snapshot in the group holds the original state.
            foreach (var group in pending.GroupBy(p => p.e.Target))
            {
                ct.ThrowIfCancellationRequested();
                var original = group.Last().e;
                try
                {
                    await ApplyAsync(original, ct);
                    foreach (var (_, e) in group) { e.Restored = true; e.RestoredAt = DateTime.Now; }
                    ok++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{original.Display}: {ex.Message}");
                    Log.Error(ex, "Restore failed for {Target}", original.Target);
                }
            }

            foreach (var r in records.Where(r => pending.Any(p => p.r == r)))
            {
                try { File.WriteAllText(r.FilePath, JsonSerializer.Serialize(r, Json.Options)); }
                catch (Exception ex) { Log.Error(ex, "Could not update backup file {File}", r.FilePath); }
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return new RestoreResult(ok, failed, errors);
        }
        finally { _gate.Release(); }
    }

    private static async Task ApplyAsync(BackupEntry e, CancellationToken ct)
    {
        switch (e.Kind)
        {
            case BackupKind.Service:
                var type = Enum.TryParse<StartupType>(e.StartupType, out var t) ? t : StartupType.Manual;
                if (type is StartupType.Automatic or StartupType.AutomaticDelayed or StartupType.Manual or StartupType.Disabled
                    && ServiceControl.GetStartupType(e.ServiceName!) != type)
                    ServiceControl.SetStartupType(e.ServiceName!, type);
                if (e.WasRunning && !ServiceControl.IsRunning(e.ServiceName!))
                    await ServiceControl.StartAsync(e.ServiceName!, ct);
                Log.Information("Restored service {Service} -> {Type}, running={Running}", e.ServiceName, type, e.WasRunning);
                break;

            case BackupKind.Registry:
                RegistryHelper.Restore(e);
                Log.Information("Restored registry {Target}", e.Target);
                break;

            case BackupKind.Wallpaper:
                WallpaperNative.RestoreFromBackup(e);
                Log.Information("Restored wallpaper {Path}", e.WallpaperPath);
                break;
        }
    }
}
