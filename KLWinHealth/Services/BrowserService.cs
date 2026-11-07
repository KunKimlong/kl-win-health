using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KLWinHealth.Models;
using Microsoft.Win32;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>
/// Browser cleaner engine: detects Chrome / Edge / Brave / Firefox, labels their processes from the command line,
/// reads installed extensions, manages memory-related policies (with backup) and clears caches.
/// </summary>
public class BrowserService
{
    private readonly BackupService _backups;
    private readonly Dictionary<int, (TimeSpan cpu, DateTime at)> _cpuSamples = new();

    public BrowserService(BackupService backups) => _backups = backups;

    // ------------------------------------------------------------------ detection
    public List<BrowserInfo> Detect()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        BrowserInfo Make(BrowserId id, string name, string proc, string exeName, string[] exeCandidates, string? userData,
            string? policy, string extUrl, string? localProfiles = null)
        {
            var exe = AppPath(exeName) ?? exeCandidates.FirstOrDefault(File.Exists);
            return new BrowserInfo
            {
                Id = id, Name = name, ProcessName = proc, ExePath = exe, UserDataDir = userData, LocalProfilesDir = localProfiles,
                PolicyKey = policy, ExtensionsUrl = extUrl,
                Installed = exe != null || (userData != null && Directory.Exists(userData))
            };
        }

        return new List<BrowserInfo>
        {
            Make(BrowserId.Chrome, "Google Chrome", "chrome", "chrome.exe",
                new[] { Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"), Path.Combine(local, @"Google\Chrome\Application\chrome.exe") },
                Path.Combine(local, @"Google\Chrome\User Data"), @"SOFTWARE\Policies\Google\Chrome", "chrome://extensions"),
            Make(BrowserId.Edge, "Microsoft Edge", "msedge", "msedge.exe",
                new[] { Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe") },
                Path.Combine(local, @"Microsoft\Edge\User Data"), @"SOFTWARE\Policies\Microsoft\Edge", "edge://extensions"),
            Make(BrowserId.Brave, "Brave", "brave", "brave.exe",
                new[] { Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(pf86, @"BraveSoftware\Brave-Browser\Application\brave.exe"), Path.Combine(local, @"BraveSoftware\Brave-Browser\Application\brave.exe") },
                Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), @"SOFTWARE\Policies\BraveSoftware\Brave", "brave://extensions"),
            Make(BrowserId.Firefox, "Mozilla Firefox", "firefox", "firefox.exe",
                new[] { Path.Combine(pf, @"Mozilla Firefox\firefox.exe"), Path.Combine(pf86, @"Mozilla Firefox\firefox.exe") },
                Path.Combine(roaming, @"Mozilla\Firefox\Profiles"), null, "about:addons", Path.Combine(local, @"Mozilla\Firefox\Profiles"))
        };
    }

    private static string? AppPath(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        return null;
    }

    public static bool IsRunning(BrowserInfo b) => Process.GetProcessesByName(b.ProcessName).Length > 0;

    // ------------------------------------------------------------------ processes
    public Task<List<BrowserProcess>> GetProcessesAsync(BrowserInfo b, CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<BrowserProcess>();
        using var searcher = new ManagementObjectSearcher(
            $"SELECT ProcessId, ParentProcessId, CommandLine, WorkingSetSize, PrivatePageCount FROM Win32_Process WHERE Name='{b.ProcessName}.exe'");
        using var results = searcher.Get();
        var now = DateTime.UtcNow;
        var titles = WindowTitlesByPid();
        foreach (ManagementObject mo in results)
        {
            ct.ThrowIfCancellationRequested();
            using (mo)
            {
                var pid = Convert.ToInt32(mo["ProcessId"]);
                var cmd = mo["CommandLine"] as string ?? "";
                var (label, detail, safety) = b.IsChromium ? ClassifyChromium(cmd) : ClassifyFirefox(cmd);
                if (titles.TryGetValue(pid, out var t) && t.Count > 0)
                    detail = (detail.Length > 0 ? detail + " · " : "") + "Windows: " + string.Join(" | ", t);
                var p = new BrowserProcess
                {
                    Pid = pid,
                    ParentPid = Convert.ToInt32(mo["ParentProcessId"]),
                    Label = label,
                    Detail = detail,
                    CommandLine = cmd,
                    Safety = safety,
                    WorkingSet = Convert.ToInt64(mo["WorkingSetSize"] ?? 0L),
                    PrivateBytes = Convert.ToInt64(mo["PrivatePageCount"] ?? 0L)
                };
                p.CpuPercent = SampleCpu(pid, now);
                list.Add(p);
            }
        }
        // Forget CPU samples for exited processes.
        foreach (var gone in _cpuSamples.Keys.Except(list.Select(p => p.Pid)).ToList()) _cpuSamples.Remove(gone);
        return list.OrderBy(p => p.Safety == ProcessSafety.Protected && p.Label == "Browser (main)" ? 0 : 1)
                   .ThenByDescending(p => p.PrivateBytes).ToList();
    }, ct);

    private double SampleCpu(int pid, DateTime now)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var cpu = proc.TotalProcessorTime;
            double pct = 0;
            lock (_cpuSamples)
            {
                if (_cpuSamples.TryGetValue(pid, out var prev))
                {
                    var wall = (now - prev.at).TotalMilliseconds;
                    if (wall > 0) pct = Math.Clamp((cpu - prev.cpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100, 0, 100);
                }
                _cpuSamples[pid] = (cpu, now);
            }
            return pct;
        }
        catch { return 0; }
    }

    private static readonly Regex TypeArg = new(@"--type=(?<t>[\w-]+)", RegexOptions.Compiled);
    private static readonly Regex SubTypeArg = new(@"--utility-sub-type=(?<s>[\w.]+)", RegexOptions.Compiled);

    public static (string label, string detail, ProcessSafety safety) ClassifyChromium(string cmd)
    {
        var type = TypeArg.Match(cmd) is { Success: true } m ? m.Groups["t"].Value : null;
        switch (type)
        {
            case null:
                return ("Browser (main)", "Closing it closes the browser", ProcessSafety.Protected);
            case "gpu-process":
                return ("GPU process", "Restarts itself; tabs may flicker", ProcessSafety.Caution);
            case "crashpad-handler":
                return ("Crash reporter", "Only uploads crash reports", ProcessSafety.Safe);
            case "renderer" when cmd.Contains("--extension-process", StringComparison.Ordinal):
                return ("Extension process", "Ending it stops the extension until it reloads", ProcessSafety.Safe);
            case "renderer":
                return ("Tab renderer", "Ending it crashes a tab", ProcessSafety.Protected);
            case "utility":
                var sub = SubTypeArg.Match(cmd) is { Success: true } s ? s.Groups["s"].Value : "";
                return sub switch
                {
                    "network.mojom.NetworkService" => ("Network service", "All network traffic goes through it", ProcessSafety.Protected),
                    "storage.mojom.StorageService" => ("Storage service", "Cookies, local storage, IndexedDB", ProcessSafety.Protected),
                    "audio.mojom.AudioService" => ("Audio service", "Sound in tabs; restarts when needed", ProcessSafety.Caution),
                    _ => ("Utility", string.IsNullOrEmpty(sub) ? "Helper process" : sub, ProcessSafety.Caution)
                };
            default:
                return ($"Other ({type})", "", ProcessSafety.Caution);
        }
    }

    public static (string label, string detail, ProcessSafety safety) ClassifyFirefox(string cmd)
    {
        if (!cmd.Contains("-contentproc", StringComparison.Ordinal))
            return ("Browser (main)", "Closing it closes Firefox", ProcessSafety.Protected);
        var last = cmd.TrimEnd().Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim('"') ?? "";
        return last switch
        {
            "tab" => ("Tab content", "Ending it crashes tabs", ProcessSafety.Protected),
            "gpu" => ("GPU process", "Restarts itself", ProcessSafety.Caution),
            "socket" => ("Network (socket)", "Network traffic", ProcessSafety.Protected),
            "rdd" => ("Media decoder (RDD)", "Video/audio decoding; restarts when needed", ProcessSafety.Caution),
            "utility" => ("Utility", "Helper process", ProcessSafety.Caution),
            _ => ($"Content ({last})", "", ProcessSafety.Caution)
        };
    }

    private static Dictionary<int, List<string>> WindowTitlesByPid()
    {
        var map = new Dictionary<int, List<string>>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            var len = Native.GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            Native.GetWindowText(h, sb, sb.Capacity);
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!map.TryGetValue((int)pid, out var l)) map[(int)pid] = l = new List<string>();
            l.Add(sb.ToString());
            return true;
        }, IntPtr.Zero);
        return map;
    }

    public static void EndProcess(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: false);
        Log.Information("Ended browser process {Pid}", pid);
    }

    /// <summary>Closes all browser windows politely (WM_CLOSE), waits, then force-ends what's left if <paramref name="force"/>.</summary>
    public async Task<bool> CloseBrowserAsync(BrowserInfo b, bool force, CancellationToken ct)
    {
        var pids = Process.GetProcessesByName(b.ProcessName).Select(p => { var id = p.Id; p.Dispose(); return id; }).ToHashSet();
        if (pids.Count == 0) return true;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && Native.IsWindowVisible(h)) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);

        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250, ct);
            if (!IsRunning(b)) { Log.Information("{Browser} closed", b.Name); return true; }
        }
        if (!force) return false;
        foreach (var p in Process.GetProcessesByName(b.ProcessName))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
        await Task.Delay(500, ct);
        Log.Information("{Browser} force-closed", b.Name);
        return !IsRunning(b);
    }

    // ------------------------------------------------------------------ profiles & extensions
    public static List<(string dir, string name)> ChromiumProfiles(BrowserInfo b)
    {
        var list = new List<(string, string)>();
        if (b.UserDataDir == null || !Directory.Exists(b.UserDataDir)) return list;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(b.UserDataDir, "Local State")));
            if (doc.RootElement.TryGetProperty("profile", out var prof) && prof.TryGetProperty("info_cache", out var cache))
                foreach (var p in cache.EnumerateObject())
                    names[p.Name] = p.Value.TryGetProperty("name", out var n) ? n.GetString() ?? p.Name : p.Name;
        }
        catch { }
        foreach (var dir in Directory.EnumerateDirectories(b.UserDataDir))
        {
            var d = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "Preferences"))) continue;
            if (d.Equals("System Profile", StringComparison.OrdinalIgnoreCase) || d.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add((dir, names.TryGetValue(d, out var n) ? $"{n} ({d})" : d));
        }
        return list.OrderBy(p => p.Item1.EndsWith("Default") ? 0 : 1).ThenBy(p => p.Item2).ToList();
    }

    public static List<(string dir, string name)> FirefoxProfiles(BrowserInfo b)
    {
        if (b.UserDataDir == null || !Directory.Exists(b.UserDataDir)) return new();
        return Directory.EnumerateDirectories(b.UserDataDir).Select(d => (d, Path.GetFileName(d))).ToList();
    }

    public Task<List<ExtensionInfo>> GetExtensionsAsync(BrowserInfo b, CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<ExtensionInfo>();
        if (b.IsChromium)
        {
            foreach (var (dir, profile) in ChromiumProfiles(b))
            {
                ct.ThrowIfCancellationRequested();
                var states = ReadChromiumExtensionStates(dir);
                var extRoot = Path.Combine(dir, "Extensions");
                if (!Directory.Exists(extRoot)) continue;
                foreach (var idDir in Directory.EnumerateDirectories(extRoot))
                {
                    var id = Path.GetFileName(idDir);
                    if (id == "Temp") continue;
                    var verDir = Directory.EnumerateDirectories(idDir)
                        .OrderByDescending(v => Version.TryParse(Path.GetFileName(v).Split('_')[0], out var ver) ? ver : new Version(0, 0))
                        .FirstOrDefault();
                    if (verDir == null) continue;
                    var (name, version) = ReadManifest(verDir);
                    list.Add(new ExtensionInfo
                    {
                        Id = id, Name = name ?? id, Version = version ?? Path.GetFileName(verDir),
                        Enabled = states.TryGetValue(id, out var en) ? en : null,
                        Size = DirSize(idDir), Profile = profile, Path = verDir
                    });
                }
            }
        }
        else
        {
            foreach (var (dir, profile) in FirefoxProfiles(b))
            {
                var json = Path.Combine(dir, "extensions.json");
                if (!File.Exists(json)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    foreach (var a in doc.RootElement.GetProperty("addons").EnumerateArray())
                    {
                        if (a.GetProperty("type").GetString() != "extension") continue;
                        if (a.TryGetProperty("location", out var loc) && loc.GetString() != "app-profile") continue;
                        var name = a.TryGetProperty("defaultLocale", out var dl) && dl.TryGetProperty("name", out var n) ? n.GetString() : null;
                        var path = a.TryGetProperty("path", out var pth) ? pth.GetString() ?? "" : "";
                        list.Add(new ExtensionInfo
                        {
                            Id = a.GetProperty("id").GetString() ?? "",
                            Name = name ?? a.GetProperty("id").GetString() ?? "",
                            Version = a.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                            Enabled = a.TryGetProperty("active", out var act) ? act.GetBoolean() : null,
                            Size = File.Exists(path) ? new FileInfo(path).Length : Directory.Exists(path) ? DirSize(path) : 0,
                            Profile = profile, Path = path
                        });
                    }
                }
                catch (Exception ex) { Log.Debug(ex, "Firefox extensions.json unreadable in {Dir}", dir); }
            }
        }
        return list.OrderBy(e => e.Profile).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }, ct);

    private static Dictionary<string, bool> ReadChromiumExtensionStates(string profileDir)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "Preferences", "Secure Preferences" })
        {
            try
            {
                var path = Path.Combine(profileDir, file);
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings)) continue;
                foreach (var e in settings.EnumerateObject())
                {
                    bool disabled = false;
                    if (e.Value.TryGetProperty("disable_reasons", out var dr))
                        disabled = dr.ValueKind == JsonValueKind.Array ? dr.GetArrayLength() > 0 : dr.ValueKind == JsonValueKind.Number && dr.GetInt32() != 0;
                    if (e.Value.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() == 0) disabled = true;
                    map[e.Name] = !disabled;
                }
            }
            catch (Exception ex) { Log.Debug(ex, "Could not read {File} in {Dir}", file, profileDir); }
        }
        return map;
    }

    private static (string? name, string? version) ReadManifest(string verDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(verDir, "manifest.json")),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var locale = root.TryGetProperty("default_locale", out var dl) ? dl.GetString() : null;
            if (name != null && name.StartsWith("__MSG_", StringComparison.Ordinal))
                name = ResolveMessage(verDir, name[6..].TrimEnd('_'), locale) ?? name;
            return (name, version);
        }
        catch { return (null, null); }
    }

    private static string? ResolveMessage(string verDir, string key, string? defaultLocale)
    {
        var candidates = new[] { "en", "en_US", "en_GB", defaultLocale }.Where(l => !string.IsNullOrEmpty(l)).Distinct();
        foreach (var loc in candidates)
        {
            var file = Path.Combine(verDir, "_locales", loc!, "messages.json");
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var m))
                        return m.GetString();
            }
            catch { }
        }
        return null;
    }

    public static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new System.IO.EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                try { total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    // ------------------------------------------------------------------ policies
    public List<PolicySetting> GetPolicies(IEnumerable<BrowserInfo> browsers)
    {
        var list = new List<PolicySetting>();
        foreach (var b in browsers.Where(b => b.Installed && b.IsChromium && b.PolicyKey != null))
        {
            list.Add(new PolicySetting
            {
                Browser = b.Id, BrowserName = b.Name, KeyPath = b.PolicyKey!, ValueName = "BackgroundModeEnabled", OptimizedValue = 0,
                Name = "Don't keep running in the background when closed",
                Description = "Turns off \"Continue running background apps when " + b.Name + " is closed\"."
            });
            if (b.Id == BrowserId.Edge)
            {
                list.Add(new PolicySetting
                {
                    Browser = b.Id, BrowserName = b.Name, KeyPath = b.PolicyKey!, ValueName = "StartupBoostEnabled", OptimizedValue = 0,
                    Name = "Turn off Startup Boost",
                    Description = "Stops Edge from pre-loading at Windows sign-in."
                });
                list.Add(new PolicySetting
                {
                    Browser = b.Id, BrowserName = b.Name, KeyPath = b.PolicyKey!, ValueName = "SleepingTabsEnabled", OptimizedValue = 1,
                    Name = "Turn on Sleeping tabs",
                    Description = "Puts background tabs to sleep to free memory."
                });
            }
            else
            {
                list.Add(new PolicySetting
                {
                    Browser = b.Id, BrowserName = b.Name, KeyPath = b.PolicyKey!, ValueName = "HighEfficiencyModeEnabled", OptimizedValue = 1,
                    Name = "Turn on Memory Saver",
                    Description = "Frees memory from inactive tabs (policy HighEfficiencyModeEnabled)."
                });
            }
        }
        foreach (var p in list) RefreshPolicy(p);
        return list;
    }

    public void RefreshPolicy(PolicySetting p)
    {
        var v = RegistryHelper.Read("HKLM", p.KeyPath, p.ValueName);
        if (v is int i)
        {
            p.IsApplied = i == p.OptimizedValue;
            p.StateText = p.IsApplied ? "Applied" : $"Set to {i} by another policy";
        }
        else
        {
            p.IsApplied = false;
            p.StateText = "Not set (browser default)";
        }
    }

    public void ApplyPolicy(PolicySetting p)
    {
        _backups.Create(Modules.Browser, $"{p.BrowserName}: {p.Name}", new[] { RegistryHelper.Snapshot("HKLM", p.KeyPath, p.ValueName) });
        RegistryHelper.Write("HKLM", p.KeyPath, p.ValueName, p.OptimizedValue, RegistryValueKind.DWord);
        Log.Information("Policy applied: HKLM\\{Key}\\{Value} = {Data}", p.KeyPath, p.ValueName, p.OptimizedValue);
        RefreshPolicy(p);
    }

    public async Task<bool> UndoPolicyAsync(PolicySetting p, CancellationToken ct)
    {
        var r = await _backups.RestoreRegistryValueAsync("HKLM", p.KeyPath, p.ValueName, ct);
        RefreshPolicy(p);
        return r.Restored > 0 && r.Failed == 0;
    }

    // ------------------------------------------------------------------ cache
    public List<CacheEntry> GetCacheEntries(IEnumerable<BrowserInfo> browsers)
    {
        var list = new List<CacheEntry>();
        foreach (var b in browsers.Where(b => b.Installed))
        {
            if (b.IsChromium)
            {
                foreach (var (dir, name) in ChromiumProfiles(b))
                {
                    list.Add(new CacheEntry
                    {
                        Browser = b.Id, BrowserName = b.Name, Profile = name,
                        Folders = new()
                        {
                            ("Cache", Path.Combine(dir, "Cache")),
                            ("Code Cache", Path.Combine(dir, "Code Cache")),
                            ("GPUCache", Path.Combine(dir, "GPUCache")),
                            ("Service Worker", Path.Combine(dir, @"Service Worker\CacheStorage")),
                            ("Service Worker", Path.Combine(dir, @"Service Worker\ScriptCache"))
                        }
                    });
                }
            }
            else if (b.LocalProfilesDir != null && Directory.Exists(b.LocalProfilesDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(b.LocalProfilesDir))
                    list.Add(new CacheEntry
                    {
                        Browser = b.Id, BrowserName = b.Name, Profile = Path.GetFileName(dir),
                        Folders = new() { ("Cache", Path.Combine(dir, "cache2")) }
                    });
            }
        }
        return list;
    }

    public static void MeasureCache(CacheEntry e)
    {
        long cache = 0, code = 0, gpu = 0, sw = 0;
        foreach (var (label, path) in e.Folders)
        {
            if (!Directory.Exists(path)) continue;
            var size = DirSize(path);
            switch (label)
            {
                case "Cache": cache += size; break;
                case "Code Cache": code += size; break;
                case "GPUCache": gpu += size; break;
                default: sw += size; break;
            }
        }
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            e.CacheSize = cache; e.CodeCacheSize = code; e.GpuCacheSize = gpu; e.ServiceWorkerSize = sw;
            e.TotalSize = cache + code + gpu + sw;
        });
    }

    /// <summary>Deletes cache files only (never cookies, passwords, history or bookmarks). Locked files are skipped.</summary>
    public static CleanResult ClearCache(CacheEntry e, CancellationToken ct)
    {
        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var (_, path) in e.Folders)
        {
            if (!Directory.Exists(path)) continue;
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", new System.IO.EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var len = f.Length;
                    f.Delete();
                    freed += len; deleted++;
                }
                catch { skipped++; }
            }
        }
        Log.Information("Cleared {Browser}/{Profile} cache: {Freed} bytes, {Deleted} files, {Skipped} skipped", e.BrowserName, e.Profile, freed, deleted, skipped);
        return new CleanResult(freed, deleted, skipped, new List<string>());
    }
}
