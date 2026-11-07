using CommunityToolkit.Mvvm.ComponentModel;

namespace KLWinHealth.Models;

public enum BrowserId { Chrome, Edge, Brave, Firefox }

public enum ProcessSafety { Protected, Caution, Safe }

public class BrowserInfo
{
    public BrowserId Id { get; init; }
    public string Name { get; init; } = "";
    public string ProcessName { get; init; } = "";       // without .exe
    public string? ExePath { get; init; }
    public string? UserDataDir { get; init; }            // Chromium "User Data" / Firefox roaming Profiles
    public string? LocalProfilesDir { get; init; }       // Firefox local (cache) profiles
    public string? PolicyKey { get; init; }              // HKLM path, Chromium only
    public string ExtensionsUrl { get; init; } = "";
    public bool IsChromium => Id != BrowserId.Firefox;
    public bool Installed { get; init; }
    public override string ToString() => Name;
}

/// <summary>One browser process with a readable label.</summary>
public partial class BrowserProcess : ObservableObject
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    public string CommandLine { get; init; } = "";
    public ProcessSafety Safety { get; init; }

    [ObservableProperty] private long _workingSet;
    [ObservableProperty] private long _privateBytes;
    [ObservableProperty] private double _cpuPercent;

    public bool CanEnd => Safety == ProcessSafety.Safe;
    public string SafetyText => Safety switch
    {
        ProcessSafety.Safe => "Safe to end",
        ProcessSafety.Caution => "Caution",
        _ => "Protected"
    };
    public string SafetyStatus => Safety switch
    {
        ProcessSafety.Safe => "Safe",
        ProcessSafety.Caution => "Caution",
        _ => "Protected"
    };
}

public class ExtensionInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public bool? Enabled { get; init; }
    public long Size { get; init; }
    public string Profile { get; init; } = "";
    public string Path { get; init; } = "";
    public string EnabledText => Enabled switch { true => "Enabled", false => "Disabled", _ => "Unknown" };
    public string EnabledStatus => Enabled switch { true => "Enabled", false => "Other", _ => "Unknown" };
}

/// <summary>A browser policy toggle (HKLM\SOFTWARE\Policies\...).</summary>
public partial class PolicySetting : ObservableObject
{
    public BrowserId Browser { get; init; }
    public string BrowserName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public string ValueName { get; init; } = "";
    public int OptimizedValue { get; init; }

    [ObservableProperty] private bool _isApplied;
    [ObservableProperty] private string _stateText = "";
}

/// <summary>Cache sizes for one browser profile.</summary>
public partial class CacheEntry : ObservableObject
{
    public BrowserId Browser { get; init; }
    public string BrowserName { get; init; } = "";
    public string Profile { get; init; } = "";
    public List<(string Label, string Path)> Folders { get; init; } = new();

    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private long _cacheSize;
    [ObservableProperty] private long _codeCacheSize;
    [ObservableProperty] private long _gpuCacheSize;
    [ObservableProperty] private long _serviceWorkerSize;
    [ObservableProperty] private long _totalSize;
}
