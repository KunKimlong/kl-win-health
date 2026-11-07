using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KLWinHealth.Models;

public enum Verdict { Clean, Suspicious, Dangerous }

public record Finding(string Text, int Score, string Category)
{
    public string Display => Score > 0 ? $"{Text} (+{Score})" : Score < 0 ? $"{Text} ({Score})" : Text;
}

/// <summary>Result of scanning one file. Observable because Defender/VirusTotal results arrive later.</summary>
public partial class ScanResult : ObservableObject
{
    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    [ObservableProperty] private long _size;
    [ObservableProperty] private string _fileType = "";
    [ObservableProperty] private string? _sha256;
    [ObservableProperty] private int _score;
    [ObservableProperty] private Verdict _verdict;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _signer = "";
    [ObservableProperty] private string _defenderResult = "Pending…";
    [ObservableProperty] private string? _defenderThreat;
    [ObservableProperty] private string _virusTotalResult = "Not checked";
    [ObservableProperty] private string? _virusTotalLink;
    [ObservableProperty] private int _virusTotalMalicious = -1;
    [ObservableProperty] private bool _isQuarantined;
    [ObservableProperty] private string? _error;

    public ObservableCollection<Finding> Findings { get; } = new();

    public ScanResult(string path) => FullPath = path;

    /// <summary>Recomputes score, verdict and summary from findings + Defender + VirusTotal.</summary>
    public void Recalculate()
    {
        var score = Findings.Sum(f => f.Score);
        if (VirusTotalMalicious >= 10) score = Math.Max(score, 90);
        else if (VirusTotalMalicious >= 3) score += 45;
        else if (VirusTotalMalicious >= 1) score += 20;
        score = Math.Clamp(score, 0, 100);
        if (DefenderThreat != null) score = 100;

        Score = score;
        Verdict = DefenderThreat != null || score >= 70 ? Verdict.Dangerous : score >= 30 ? Verdict.Suspicious : Verdict.Clean;

        var parts = new List<string>();
        if (DefenderThreat != null) parts.Add("Defender: " + DefenderThreat);
        if (VirusTotalMalicious > 0) parts.Add($"VirusTotal: {VirusTotalResult}");
        parts.AddRange(Findings.Where(f => f.Score > 0).OrderByDescending(f => f.Score).Select(f => f.Text));
        Summary = parts.Count == 0 ? (Error ?? "No issues found") : string.Join(" · ", parts);
    }
}

/// <summary>Record stored next to each quarantined file (&lt;id&gt;.json).</summary>
public class QuarantineRecord
{
    public string Id { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public DateTime QuarantinedAt { get; set; }
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    public string? Reason { get; set; }
    public string QuarantineFile { get; set; } = "";

    public string FileName => Path.GetFileName(OriginalPath);
    public string DateText => QuarantinedAt.ToString("yyyy-MM-dd HH:mm");
}
