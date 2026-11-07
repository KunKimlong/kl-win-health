using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>
/// Shared live system statistics. CPU/RAM (and GPU/disk/network when a widget needs them) every 2s,
/// disk free space and battery every 30s. All sampling happens off the UI thread.
/// </summary>
public partial class SystemStatsService : ObservableObject, IDisposable
{
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private ulong _ramUsed;
    [ObservableProperty] private ulong _ramTotal;
    [ObservableProperty] private long _systemDriveFree;
    [ObservableProperty] private long _systemDriveTotal;
    [ObservableProperty] private double _gpuPercent;
    [ObservableProperty] private double _diskActivityPercent;
    [ObservableProperty] private double _netDownBytesPerSec;
    [ObservableProperty] private double _netUpBytesPerSec;
    [ObservableProperty] private int _batteryPercent = -1;
    [ObservableProperty] private bool _batteryCharging;
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private bool _onAcPower = true;

    private readonly System.Threading.Timer _fastTimer;
    private readonly System.Threading.Timer _slowTimer;
    private long _prevIdle, _prevKernel, _prevUser;
    private int _extendedUsers;
    private readonly object _sampleLock = new();

    // GPU ("GPU Engine" counters, 3D engines summed per adapter → max)
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new();
    private DateTime _gpuInstancesRefreshed = DateTime.MinValue;
    private PerformanceCounter? _diskIdle;
    private long _prevNetRx, _prevNetTx;
    private DateTime _prevNetTime;

    public SystemStatsService()
    {
        Native.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
        _fastTimer = new System.Threading.Timer(_ => SampleFast(), null, 500, 2000);
        _slowTimer = new System.Threading.Timer(_ => SampleSlow(), null, 0, 30000);
    }

    /// <summary>Widgets call this so GPU/disk/network are only sampled while someone is showing them.</summary>
    public IDisposable RequestExtendedStats()
    {
        Interlocked.Increment(ref _extendedUsers);
        return new Releaser(() => Interlocked.Decrement(ref _extendedUsers));
    }

    /// <summary>Re-reads free disk space now (after a clean).</summary>
    public void RefreshDiskNow() => ThreadPool.QueueUserWorkItem(_ => SampleSlow());

    private void SampleFast()
    {
        if (!Monitor.TryEnter(_sampleLock)) return;
        try
        {
            double cpu = 0;
            if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var idleD = idle - _prevIdle;
                var total = (kernel - _prevKernel) + (user - _prevUser); // kernel includes idle
                if (total > 0) cpu = Math.Clamp((total - idleD) * 100.0 / total, 0, 100);
                _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            }

            var mem = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
            Native.GlobalMemoryStatusEx(ref mem);

            // CPU/RAM first: the extended counters below can be slow (GPU instance discovery takes seconds the first time).
            Post(() =>
            {
                CpuPercent = cpu;
                RamTotal = mem.ullTotalPhys;
                RamUsed = mem.ullTotalPhys - mem.ullAvailPhys;
                RamPercent = mem.ullTotalPhys == 0 ? 0 : RamUsed * 100.0 / mem.ullTotalPhys;
            });

            if (_extendedUsers <= 0) return;
            var gpu = SampleGpu();
            var disk = SampleDisk();
            var (down, up) = SampleNetwork();
            Post(() =>
            {
                GpuPercent = gpu;
                DiskActivityPercent = disk;
                NetDownBytesPerSec = down;
                NetUpBytesPerSec = up;
            });
        }
        catch (Exception ex) { Log.Debug(ex, "Stats sample failed"); }
        finally { Monitor.Exit(_sampleLock); }
    }

    private void SampleSlow()
    {
        try
        {
            var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var di = new DriveInfo(sysDrive);
            long free = di.IsReady ? di.AvailableFreeSpace : 0, total = di.IsReady ? di.TotalSize : 0;

            Native.GetSystemPowerStatus(out var ps);
            bool hasBattery = ps.BatteryFlag != 128 && ps.BatteryFlag != 255 && ps.BatteryLifePercent != 255;
            bool charging = (ps.BatteryFlag & 8) != 0;
            Post(() =>
            {
                SystemDriveFree = free;
                SystemDriveTotal = total;
                HasBattery = hasBattery;
                BatteryPercent = hasBattery ? ps.BatteryLifePercent : -1;
                BatteryCharging = charging;
                OnAcPower = ps.ACLineStatus == 1;
            });
        }
        catch (Exception ex) { Log.Debug(ex, "Slow stats sample failed"); }
    }

    private double SampleGpu()
    {
        try
        {
            if (DateTime.UtcNow - _gpuInstancesRefreshed > TimeSpan.FromSeconds(10))
            {
                _gpuInstancesRefreshed = DateTime.UtcNow;
                if (!PerformanceCounterCategory.Exists("GPU Engine")) return 0;
                var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                    .Where(n => n.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)).ToHashSet();
                foreach (var gone in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpuCounters[gone].Dispose();
                    _gpuCounters.Remove(gone);
                }
                foreach (var n in names.Where(n => !_gpuCounters.ContainsKey(n)))
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true);
                    try { pc.NextValue(); } catch { }
                    _gpuCounters[n] = pc;
                }
            }

            // Sum per physical adapter ("luid_0x..._0x..._phys_N"), report the busiest adapter.
            var perAdapter = new Dictionary<string, double>();
            foreach (var (name, pc) in _gpuCounters)
            {
                double v;
                try { v = pc.NextValue(); } catch { continue; }
                var i = name.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                var j = name.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
                var adapter = i >= 0 && j > i ? name[i..j] : "gpu";
                perAdapter[adapter] = perAdapter.GetValueOrDefault(adapter) + v;
            }
            return perAdapter.Count == 0 ? 0 : Math.Clamp(perAdapter.Values.Max(), 0, 100);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "GPU sample failed");
            return 0;
        }
    }

    private double SampleDisk()
    {
        try
        {
            _diskIdle ??= new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", true);
            return Math.Clamp(100 - _diskIdle.NextValue(), 0, 100);
        }
        catch { return 0; }
    }

    private (double down, double up) SampleNetwork()
    {
        long rx = 0, tx = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            catch { }
        }
        var now = DateTime.UtcNow;
        double down = 0, up = 0;
        if (_prevNetTime != default)
        {
            var secs = (now - _prevNetTime).TotalSeconds;
            if (secs > 0)
            {
                down = Math.Max(0, (rx - _prevNetRx) / secs);
                up = Math.Max(0, (tx - _prevNetTx) / secs);
            }
        }
        _prevNetRx = rx; _prevNetTx = tx; _prevNetTime = now;
        return (down, up);
    }

    private static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.HasShutdownStarted) return;
        d.BeginInvoke(a);
    }

    public void Dispose()
    {
        _fastTimer.Dispose();
        _slowTimer.Dispose();
        foreach (var pc in _gpuCounters.Values) pc.Dispose();
        _diskIdle?.Dispose();
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
