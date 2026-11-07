using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>A cleanup category (from config/cleanup_categories.json) with its analyzed size.</summary>
public partial class CleanupCategory : ObservableObject
{
    public CleanupCategoryDef Def { get; }
    public string Name => Def.Name;
    public string Description => Def.Description;
    public bool IsCaution => Def.Level.Equals("Caution", StringComparison.OrdinalIgnoreCase);
    public string Level => IsCaution ? "Caution" : "Safe";
    public bool IsShortcut => Def.Kind == "BrowserShortcut";

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private long _size = -1;
    [ObservableProperty] private long _fileCount;
    [ObservableProperty] private string _status = "Not analyzed";
    [ObservableProperty] private bool _isAnalyzed;

    public CleanupCategory(CleanupCategoryDef def)
    {
        Def = def;
        IsChecked = !IsCaution;
        if (IsShortcut) Status = "Opens Browser cleaner";
        if (def.Kind == "Dism") Status = "Size is calculated by DISM";
    }
}

public record CleanResult(long BytesFreed, int FilesDeleted, int FilesSkipped, List<string> Notes)
{
    public static CleanResult Empty => new(0, 0, 0, new List<string>());
    public CleanResult Add(CleanResult o) => new(BytesFreed + o.BytesFreed, FilesDeleted + o.FilesDeleted,
        FilesSkipped + o.FilesSkipped, Notes.Concat(o.Notes).ToList());
}

public record LargeFile(string Name, string FullPath, long Size, DateTime LastModified)
{
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

/// <summary>Analyzes and cleans storage categories, finds large files and old downloads.</summary>
public class StorageService
{
    private readonly ConfigService _config;

    public StorageService(ConfigService config) => _config = config;

    public List<CleanupCategory> CreateCategories() =>
        _config.CleanupCategories.Categories.Select(d => new CleanupCategory(d)).ToList();

    private static EnumerationOptions Options(bool recursive) => new()
    {
        RecurseSubdirectories = recursive,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions/symlinks
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32
    };

    private static IEnumerable<string> ExpandedPaths(CleanupCategoryDef d) =>
        d.Paths.Select(p => Environment.ExpandEnvironmentVariables(p)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<FileInfo> CandidateFiles(CleanupCategoryDef d, CancellationToken ct)
    {
        var cutoff = DateTime.Now - TimeSpan.FromDays(d.MinAgeDays) - TimeSpan.FromHours(d.MinAgeHours);
        bool useAge = d.MinAgeDays > 0 || d.MinAgeHours > 0;
        foreach (var root in ExpandedPaths(d))
        {
            if (!Directory.Exists(root)) continue;
            if (!PathGuard.IsCleanableFolder(root, out var reason))
            {
                Log.Warning("Cleanup refused for {Root}: {Reason}", root, reason);
                continue;
            }
            foreach (var pattern in d.FilePatterns.DefaultIfEmpty("*"))
            {
                IEnumerable<FileInfo> files;
                try { files = new DirectoryInfo(root).EnumerateFiles(pattern, Options(d.Recursive)); }
                catch (Exception ex) { Log.Debug(ex, "Enumerate failed {Root}", root); continue; }
                using var e = files.GetEnumerator();
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    FileInfo f;
                    try
                    {
                        if (!e.MoveNext()) break;
                        f = e.Current;
                    }
                    catch (Exception ex) { Log.Debug(ex, "Enumerate step failed in {Root}", root); break; }
                    if (useAge)
                    {
                        try { if (f.LastWriteTime > cutoff) continue; } catch { continue; }
                    }
                    yield return f;
                }
            }
        }
    }

    // ------------------------------------------------------------------ analyze
    public async Task AnalyzeAsync(CleanupCategory c, CancellationToken ct)
    {
        var d = c.Def;
        await Task.Run(() =>
        {
            long size = 0, count = 0;
            switch (d.Kind)
            {
                case "RecycleBin":
                    var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
                    if (Native.SHQueryRecycleBin(null, ref info) == 0) { size = info.i64Size; count = info.i64NumItems; }
                    break;
                case "Folder":
                case "WindowsUpdate":
                case "WindowsOld":
                    foreach (var f in CandidateFiles(d, ct))
                    {
                        try { size += f.Length; count++; } catch { }
                    }
                    break;
                case "Dism":
                case "BrowserShortcut":
                    Post(() => { c.IsAnalyzed = true; });
                    return;
            }
            Post(() =>
            {
                c.Size = size;
                c.FileCount = count;
                c.IsAnalyzed = true;
                c.Status = d.Kind == "WindowsOld" && size == 0 ? "Not present" : count == 0 ? "Already clean" : $"{count:N0} files";
            });
        }, ct);
    }

    // ------------------------------------------------------------------ clean
    public async Task<CleanResult> CleanAsync(CleanupCategory c, Action<double, string>? progress, CancellationToken ct)
    {
        var d = c.Def;
        Log.Information("Cleaning category {Id} ({Name})", d.Id, d.Name);
        var result = d.Kind switch
        {
            "Folder" => await Task.Run(() => DeleteFiles(d, progress, ct), ct),
            "RecycleBin" => await Task.Run(EmptyRecycleBin, ct),
            "WindowsUpdate" => await CleanWindowsUpdateAsync(d, progress, ct),
            "WindowsOld" => await RemoveWindowsOldAsync(d, progress, ct),
            "Dism" => await RunDismAsync(progress, ct),
            _ => CleanResult.Empty
        };
        Log.Information("Category {Id}: freed {Bytes} bytes, {Deleted} deleted, {Skipped} skipped",
            d.Id, result.BytesFreed, result.FilesDeleted, result.FilesSkipped);
        return result;
    }

    private static CleanResult DeleteFiles(CleanupCategoryDef d, Action<double, string>? progress, CancellationToken ct)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;
        var files = CandidateFiles(d, ct).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = files[i];
            if (i % 50 == 0) progress?.Invoke(files.Count == 0 ? 100 : i * 100.0 / files.Count, $"{d.Name}: {i:N0} / {files.Count:N0}");
            if (!PathGuard.IsDeletableFile(f.FullName)) { skipped++; continue; }
            try
            {
                var len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                freed += len;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                skipped++; // locked / in use / protected — skip silently, counted in the summary
            }
        }

        // Remove now-empty sub-folders (never the category root itself).
        foreach (var root in ExpandedPaths(d).Where(Directory.Exists))
        {
            if (!d.Recursive || !PathGuard.IsCleanableFolder(root, out _)) continue;
            try
            {
                var dirs = new DirectoryInfo(root).EnumerateDirectories("*", Options(true))
                    .OrderByDescending(x => x.FullName.Length).ToList();
                foreach (var dir in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    try { if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete(false); } catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Debug(ex, "Removing empty folders failed in {Root}", root); }
        }
        return new CleanResult(freed, deleted, skipped, new List<string>());
    }

    private static CleanResult EmptyRecycleBin()
    {
        var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
        Native.SHQueryRecycleBin(null, ref info);
        if (info.i64NumItems == 0) return CleanResult.Empty;
        var hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
        if (hr != 0)
        {
            Log.Warning("SHEmptyRecycleBin returned 0x{Hr:X}", hr);
            return new CleanResult(0, 0, (int)info.i64NumItems, new List<string> { "The Recycle Bin could not be emptied completely." });
        }
        return new CleanResult(info.i64Size, (int)info.i64NumItems, 0, new List<string>());
    }

    private static async Task<CleanResult> CleanWindowsUpdateAsync(CleanupCategoryDef d, Action<double, string>? progress, CancellationToken ct)
    {
        const string svc = "wuauserv";
        var wasRunning = ServiceControl.IsRunning(svc);
        try
        {
            if (wasRunning)
            {
                progress?.Invoke(-1, "Stopping Windows Update service…");
                await ServiceControl.StopAsync(svc, ct);
            }
            return await Task.Run(() => DeleteFiles(d, progress, ct), ct);
        }
        finally
        {
            if (wasRunning)
            {
                progress?.Invoke(-1, "Starting Windows Update service again…");
                try { await ServiceControl.StartAsync(svc, CancellationToken.None); }
                catch (Exception ex) { Log.Error(ex, "Could not restart wuauserv after cleaning"); }
            }
        }
    }

    private static async Task<CleanResult> RemoveWindowsOldAsync(CleanupCategoryDef d, Action<double, string>? progress, CancellationToken ct)
    {
        var path = ExpandedPaths(d).FirstOrDefault();
        if (path == null || !Directory.Exists(path)) return CleanResult.Empty;
        if (!Path.GetFileName(path.TrimEnd('\\')).Equals("Windows.old", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected Windows.old path: " + path);

        long before = 0; int count = 0;
        foreach (var f in CandidateFiles(d, ct)) { try { before += f.Length; count++; } catch { } }

        progress?.Invoke(-1, "Taking ownership of Windows.old (this can take several minutes)…");
        await ProcessRunner.RunAsync("takeown.exe", $"/F \"{path}\" /R /A /D Y", ct, timeout: TimeSpan.FromMinutes(30));
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(-1, "Granting permissions…");
        await ProcessRunner.RunAsync("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /T /C /Q", ct, timeout: TimeSpan.FromMinutes(30));
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(-1, "Deleting Windows.old…");
        await ProcessRunner.RunAsync("cmd.exe", $"/c rd /s /q \"{path}\"", ct, timeout: TimeSpan.FromMinutes(60));

        long after = 0; int left = 0;
        if (Directory.Exists(path))
            foreach (var f in CandidateFiles(d, CancellationToken.None)) { try { after += f.Length; left++; } catch { } }
        var notes = left > 0 ? new List<string> { $"{left:N0} files in Windows.old could not be removed." } : new List<string>();
        return new CleanResult(before - after, count - left, left, notes);
    }

    private static async Task<CleanResult> RunDismAsync(Action<double, string>? progress, CancellationToken ct)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var freeBefore = drive.AvailableFreeSpace;
        var rx = new Regex(@"(\d{1,3}(?:[.,]\d)?)%", RegexOptions.Compiled);
        progress?.Invoke(-1, "DISM: starting component cleanup (can take 10+ minutes)…");
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var r = await ProcessRunner.RunAsync(dism, "/Online /Cleanup-Image /StartComponentCleanup /English", ct,
            chunk =>
            {
                var m = rx.Matches(chunk);
                if (m.Count > 0 && double.TryParse(m[^1].Groups[1].Value.Replace(',', '.'),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                    progress?.Invoke(pct, $"DISM component cleanup: {pct:0.#}%");
            }, TimeSpan.FromHours(2), System.Text.Encoding.Default);
        if (r.ExitCode != 0)
        {
            Log.Warning("DISM exited {Code}: {Out}", r.ExitCode, r.Output);
            throw new InvalidOperationException($"DISM finished with error code {r.ExitCode}. See the log for details.");
        }
        drive = new DriveInfo(drive.Name);
        var freed = Math.Max(0, drive.AvailableFreeSpace - freeBefore);
        return new CleanResult(freed, 0, 0, new List<string> { "Component store cleaned by DISM." });
    }

    // ------------------------------------------------------------------ tools
    /// <summary>Top-N largest files under a folder/drive (skips reparse points and inaccessible folders).</summary>
    public Task<List<LargeFile>> FindLargestFilesAsync(string root, int top, Action<long>? progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var heap = new PriorityQueue<FileInfo, long>();
            long scanned = 0;
            var opts = Options(true);
            opts.AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline;
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                long len;
                try { len = f.Length; } catch { continue; }
                if (heap.Count < top) heap.Enqueue(f, len);
                else if (heap.TryPeek(out _, out var min) && len > min) heap.EnqueueDequeue(f, len);
                if (++scanned % 2000 == 0) progress?.Invoke(scanned);
            }
            progress?.Invoke(scanned);
            var list = new List<LargeFile>();
            while (heap.TryDequeue(out var f, out var len))
            {
                DateTime lm;
                try { lm = f.LastWriteTime; } catch { lm = DateTime.MinValue; }
                list.Add(new LargeFile(f.Name, f.FullName, len, lm));
            }
            list.Reverse();
            return list;
        }, ct);

    public static string? DownloadsFolder =>
        Native.GetKnownFolder(Native.FOLDERID_Downloads) ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Files (top level and sub-folders) in Downloads not modified for <paramref name="days"/> days. Review only.</summary>
    public Task<List<LargeFile>> FindOldDownloadsAsync(int days, CancellationToken ct) =>
        Task.Run(() =>
        {
            var list = new List<LargeFile>();
            var dir = DownloadsFolder;
            if (dir == null || !Directory.Exists(dir)) return list;
            var cutoff = DateTime.Now.AddDays(-days);
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Options(true)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var t = f.LastWriteTime > f.CreationTime ? f.LastWriteTime : f.CreationTime;
                    if (t < cutoff) list.Add(new LargeFile(f.Name, f.FullName, f.Length, t));
                }
                catch { }
            }
            return list.OrderBy(f => f.LastModified).ToList();
        }, ct);

    private static void Post(Action a) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(a);
}
