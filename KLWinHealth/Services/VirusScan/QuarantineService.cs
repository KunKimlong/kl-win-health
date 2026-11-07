using System.IO;
using System.Text.Json;
using KLWinHealth.Models;
using Serilog;

namespace KLWinHealth.Services.VirusScan;

/// <summary>
/// Moves files into %ProgramData%\KLWinHealth\quarantine\&lt;id&gt;.quarantine with a &lt;id&gt;.json record of the
/// original path. The folder is admin-only by default (ProgramData ACLs) and files lose their extension, so they can't run.
/// </summary>
public class QuarantineService
{
    public QuarantineRecord Quarantine(string path, string? sha256, string? reason)
    {
        Directory.CreateDirectory(AppPaths.Quarantine);
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("The file no longer exists.", path);

        var id = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..28];
        var target = Path.Combine(AppPaths.Quarantine, id + ".quarantine");
        var record = new QuarantineRecord
        {
            Id = id,
            OriginalPath = fi.FullName,
            QuarantinedAt = DateTime.Now,
            Sha256 = sha256,
            Size = fi.Length,
            Reason = reason,
            QuarantineFile = target
        };

        if (fi.IsReadOnly) fi.IsReadOnly = false;
        File.Move(fi.FullName, target);
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Quarantine, id + ".json"), JsonSerializer.Serialize(record, Json.Options));
        }
        catch
        {
            // Never lose the user's file: if the record can't be written, put the file back.
            File.Move(target, fi.FullName);
            throw;
        }
        Log.Warning("Quarantined {Path} -> {Target} ({Reason})", fi.FullName, target, reason);
        return record;
    }

    public List<QuarantineRecord> List()
    {
        var list = new List<QuarantineRecord>();
        if (!Directory.Exists(AppPaths.Quarantine)) return list;
        foreach (var json in Directory.EnumerateFiles(AppPaths.Quarantine, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<QuarantineRecord>(File.ReadAllText(json), Json.Options);
                if (r != null && File.Exists(r.QuarantineFile)) list.Add(r);
            }
            catch (Exception ex) { Log.Warning(ex, "Bad quarantine record {File}", json); }
        }
        return list.OrderByDescending(r => r.QuarantinedAt).ToList();
    }

    /// <summary>Moves the file back. If something now exists at the original path, " (restored)" is added to the name.</summary>
    public string Restore(QuarantineRecord r)
    {
        var dest = r.OriginalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest))
        {
            var dir = Path.GetDirectoryName(dest)!;
            var name = Path.GetFileNameWithoutExtension(dest);
            var ext = Path.GetExtension(dest);
            int i = 1;
            do dest = Path.Combine(dir, $"{name} (restored{(i > 1 ? " " + i : "")}){ext}"); while (File.Exists(dest) && ++i < 100);
        }
        File.Move(r.QuarantineFile, dest);
        try { File.Delete(Path.Combine(AppPaths.Quarantine, r.Id + ".json")); } catch { }
        Log.Information("Restored from quarantine {Id} -> {Dest}", r.Id, dest);
        return dest;
    }

    public void DeletePermanently(QuarantineRecord r)
    {
        File.Delete(r.QuarantineFile);
        try { File.Delete(Path.Combine(AppPaths.Quarantine, r.Id + ".json")); } catch { }
        Log.Information("Deleted quarantined file {Id} ({Path})", r.Id, r.OriginalPath);
    }
}
