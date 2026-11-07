using System.Diagnostics;
using System.IO;
using System.Management;
using KLWinHealth.Models;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>Raw service data from WMI (Win32_Service).</summary>
public record RawService(string Name, string DisplayName, string Description, bool Running, int ProcessId, string? PathName);

/// <summary>
/// Lists services (WMI) with RAM usage, and performs Stop / Disable / Manual / Restore with a backup before every change.
/// </summary>
public class ServiceManager
{
    private readonly BackupService _backups;

    public ServiceManager(BackupService backups) => _backups = backups;

    public Task<List<RawService>> QueryAsync(CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<RawService>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, Description, State, ProcessId, PathName FROM Win32_Service");
        using var results = searcher.Get();
        foreach (ManagementObject mo in results)
        {
            ct.ThrowIfCancellationRequested();
            using (mo)
            {
                list.Add(new RawService(
                    mo["Name"] as string ?? "",
                    mo["DisplayName"] as string ?? "",
                    mo["Description"] as string ?? "",
                    string.Equals(mo["State"] as string, "Running", StringComparison.OrdinalIgnoreCase),
                    Convert.ToInt32(mo["ProcessId"] ?? 0),
                    mo["PathName"] as string));
            }
        }
        return list;
    }, ct);

    /// <summary>Builds rows; RAM is the working set of the service's own process, or "Shared host" when several services share it.</summary>
    public List<ServiceItem> BuildItems(IEnumerable<RawService> raw, Func<RawService, (RuleCategory cat, ServiceRule? rule, string text, string status)> classify)
    {
        var rawList = raw.ToList();
        var pidCounts = rawList.Where(r => r.ProcessId > 0).GroupBy(r => r.ProcessId).ToDictionary(g => g.Key, g => g.Count());
        var active = ActiveBackupNames();
        var items = new List<ServiceItem>();
        foreach (var r in rawList)
        {
            var (cat, rule, text, status) = classify(r);
            var exe = ExtractExe(r.PathName);
            var item = new ServiceItem
            {
                Name = r.Name,
                DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Name : r.DisplayName,
                Description = r.Description,
                ExePath = exe,
                Publisher = GetPublisher(exe),
                Category = cat,
                RuleLabel = rule?.Label ?? "",
                Reason = rule?.Reason ?? "",
                CategoryText = text,
                CategoryStatus = status,
                IsProtected = cat == RuleCategory.Protected || ServiceControl.IsHardProtected(r.Name),
                IsRunning = r.Running,
                StartupType = ServiceControl.GetStartupType(r.Name),
                ProcessId = r.ProcessId,
                HasBackup = active.Contains(r.Name)
            };
            FillRam(item, pidCounts);
            items.Add(item);
        }
        return items;
    }

    private HashSet<string> ActiveBackupNames() =>
        _backups.LoadAll().SelectMany(b => b.Entries)
            .Where(e => e.Kind == BackupKind.Service && !e.Restored && e.ServiceName != null)
            .Select(e => e.ServiceName!).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void FillRam(ServiceItem item, Dictionary<int, int> pidCounts)
    {
        item.IsSharedHost = false;
        item.RamBytes = -1;
        if (!item.IsRunning || item.ProcessId <= 0) return;
        if (pidCounts.TryGetValue(item.ProcessId, out var n) && n > 1)
        {
            item.IsSharedHost = true;
            return;
        }
        try
        {
            using var p = Process.GetProcessById(item.ProcessId);
            item.RamBytes = p.WorkingSet64;
        }
        catch { /* protected process — leave "—" */ }
    }

    public static string? ExtractExe(string? pathName)
    {
        if (string.IsNullOrWhiteSpace(pathName)) return null;
        var p = Environment.ExpandEnvironmentVariables(pathName.Trim());
        if (p.StartsWith('"'))
        {
            var end = p.IndexOf('"', 1);
            return end > 1 ? p[1..end] : null;
        }
        var idx = p.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? p[..(idx + 4)] : p.Split(' ')[0];
    }

    private static readonly Dictionary<string, string?> PublisherCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? GetPublisher(string? exe)
    {
        if (exe == null) return null;
        lock (PublisherCache)
        {
            if (PublisherCache.TryGetValue(exe, out var cached)) return cached;
        }
        string? company = null;
        try { if (File.Exists(exe)) company = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim(); } catch { }
        lock (PublisherCache) PublisherCache[exe] = company;
        return company;
    }

    // ------------------------------------------------------------------ actions (each backs up first)
    private BackupRecord Backup(string module, string description, IEnumerable<ServiceItem> items) =>
        _backups.Create(module, description, items.Select(i => new BackupEntry
        {
            Kind = BackupKind.Service,
            ServiceName = i.Name,
            ServiceDisplayName = i.DisplayName,
            StartupType = ServiceControl.GetStartupType(i.Name).ToString(),
            WasRunning = ServiceControl.IsRunning(i.Name)
        }));

    public async Task StopAsync(string module, ServiceItem item, CancellationToken ct)
    {
        Guard(item);
        Backup(module, $"Stop {item.DisplayName}", new[] { item });
        await ServiceControl.StopAsync(item.Name, ct);
        Log.Information("[{Module}] Stopped {Service}", module, item.Name);
    }

    public async Task SetStartupAsync(string module, ServiceItem item, StartupType type, bool stopNow, CancellationToken ct)
    {
        Guard(item);
        Backup(module, $"{(type == StartupType.Disabled ? "Disable" : "Set to " + type)} {item.DisplayName}", new[] { item });
        ServiceControl.SetStartupType(item.Name, type);
        if (stopNow && ServiceControl.IsRunning(item.Name)) await ServiceControl.StopAsync(item.Name, ct);
        Log.Information("[{Module}] {Service} -> {Type} (stop={Stop})", module, item.Name, type, stopNow);
    }

    /// <summary>Stops + disables several services with ONE backup record. Returns per-service errors.</summary>
    public async Task<List<string>> StopAndDisableAsync(string module, IReadOnlyList<ServiceItem> items, Action<double, string>? progress, CancellationToken ct)
    {
        foreach (var i in items) Guard(i);
        Backup(module, $"Clean recommended: stop + disable {items.Count} service(s)", items);
        var errors = new List<string>();
        for (int n = 0; n < items.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var i = items[n];
            progress?.Invoke(n * 100.0 / items.Count, $"Stopping {i.DisplayName}…");
            try
            {
                ServiceControl.SetStartupType(i.Name, StartupType.Disabled);
                if (ServiceControl.IsRunning(i.Name)) await ServiceControl.StopAsync(i.Name, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warning(ex, "[{Module}] Could not stop/disable {Service}", module, i.Name);
                errors.Add($"{i.DisplayName}: {ViewModels.PageViewModel.FriendlyError(ex)}");
            }
        }
        return errors;
    }

    private static void Guard(ServiceItem item)
    {
        if (item.IsProtected || ServiceControl.IsHardProtected(item.Name))
            throw new InvalidOperationException($"{item.DisplayName} is protected and will not be changed.");
    }
}
