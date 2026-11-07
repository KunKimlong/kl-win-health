using System.ComponentModel;
using System.ServiceProcess;
using Microsoft.Win32;
using Serilog;

namespace KLWinHealth.Services;

public enum StartupType { Automatic, AutomaticDelayed, Manual, Disabled, Boot, System, Unknown }

/// <summary>Low-level start/stop/start-type operations for Windows services, with safety guards.</summary>
public static class ServiceControl
{
    /// <summary>
    /// Services this app will never stop or reconfigure, regardless of UI or config file contents.
    /// Windows Defender must always stay running (Virus check depends on it).
    /// </summary>
    private static readonly HashSet<string> HardProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "WdNisSvc", "WdNisDrv", "WdFilter", "WdBoot", "Sense", "MDCoreSvc", "SecurityHealthService",
        "wscsvc", "mpssvc", "BFE", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog",
        "Winmgmt", "PlugPlay", "Power", "ProfSvc", "CryptSvc", "UserManager", "gpsvc", "Schedule",
        "BrokerInfrastructure", "SystemEventsBroker", "CoreMessagingRegistrar", "StateRepository", "KeyIso"
    };

    public static bool IsHardProtected(string serviceName) => HardProtected.Contains(serviceName);

    public static StartupType GetStartupType(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key == null) return StartupType.Unknown;
            var start = Convert.ToInt32(key.GetValue("Start", 3));
            var delayed = Convert.ToInt32(key.GetValue("DelayedAutostart", 0)) == 1;
            return start switch
            {
                0 => StartupType.Boot,
                1 => StartupType.System,
                2 => delayed ? StartupType.AutomaticDelayed : StartupType.Automatic,
                3 => StartupType.Manual,
                4 => StartupType.Disabled,
                _ => StartupType.Unknown
            };
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GetStartupType failed for {Service}", serviceName);
            return StartupType.Unknown;
        }
    }

    public static bool IsRunning(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending
                or ServiceControllerStatus.ContinuePending or ServiceControllerStatus.Paused;
        }
        catch { return false; }
    }

    public static void SetStartupType(string serviceName, StartupType type)
    {
        Guard(serviceName);
        if (type is StartupType.Unknown or StartupType.Boot or StartupType.System)
            throw new InvalidOperationException($"Refusing to set start type '{type}' on {serviceName}.");

        uint startType = type switch
        {
            StartupType.Automatic or StartupType.AutomaticDelayed => 2,
            StartupType.Manual => 3,
            StartupType.Disabled => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };

        var scm = Native.OpenSCManager(null, null, Native.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = Native.OpenService(scm, serviceName, Native.SERVICE_CHANGE_CONFIG | Native.SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!Native.ChangeServiceConfig(svc, Native.SERVICE_NO_CHANGE, startType, Native.SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception();

                if (startType == 2)
                {
                    var info = new Native.SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = type == StartupType.AutomaticDelayed };
                    if (!Native.ChangeServiceConfig2(svc, Native.SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                        Log.Warning("Could not set delayed-auto-start on {Service}: {Err}", serviceName, new Win32Exception().Message);
                }
            }
            finally { Native.CloseServiceHandle(svc); }
        }
        finally { Native.CloseServiceHandle(scm); }
        Log.Information("Service {Service} start type set to {Type}", serviceName, type);
    }

    public static async Task StopAsync(string serviceName, CancellationToken ct = default)
    {
        Guard(serviceName);
        using var sc = new ServiceController(serviceName);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending)
        {
            if (!sc.CanStop) throw new InvalidOperationException($"{sc.DisplayName} does not accept stop requests.");
            // Refuse if a protected service depends on this one and is running.
            foreach (var dep in SafeDependents(sc))
                if (IsHardProtected(dep.ServiceName) && dep.Status != ServiceControllerStatus.Stopped)
                    throw new InvalidOperationException($"{dep.DisplayName} depends on this service and must keep running.");
            sc.Stop();
        }
        await WaitForAsync(sc, ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30), ct);
        Log.Information("Service {Service} stopped", serviceName);
    }

    public static async Task StartAsync(string serviceName, CancellationToken ct = default)
    {
        using var sc = new ServiceController(serviceName);
        if (sc.Status is ServiceControllerStatus.Running) return;
        if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
        await WaitForAsync(sc, ServiceControllerStatus.Running, TimeSpan.FromSeconds(30), ct);
        Log.Information("Service {Service} started", serviceName);
    }

    private static IEnumerable<ServiceController> SafeDependents(ServiceController sc)
    {
        try { return sc.DependentServices; } catch { return Array.Empty<ServiceController>(); }
    }

    private static async Task WaitForAsync(ServiceController sc, ServiceControllerStatus target, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            sc.Refresh();
            if (sc.Status == target) return;
            await Task.Delay(250, ct);
        }
        throw new System.TimeoutException($"{sc.DisplayName} did not reach state '{target}' within {timeout.TotalSeconds:0}s.");
    }

    private static void Guard(string serviceName)
    {
        if (IsHardProtected(serviceName))
            throw new InvalidOperationException($"{serviceName} is a protected system service and will not be changed.");
    }
}
