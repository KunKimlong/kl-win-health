using CommunityToolkit.Mvvm.ComponentModel;
using KLWinHealth.Services;

namespace KLWinHealth.Models;

/// <summary>A Windows service row in the ROG / Windows service pages.</summary>
public partial class ServiceItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ExePath { get; init; }
    public string? Publisher { get; init; }
    public RuleCategory Category { get; init; }
    /// <summary>Feature label from the rules (e.g. "Fan profiles & performance modes").</summary>
    public string RuleLabel { get; init; } = "";
    public string Reason { get; init; } = "";
    /// <summary>Badge text: Required / Critical / Safe to stop / Caution / Unknown — not recommended / Not classified.</summary>
    public string CategoryText { get; init; } = "";
    /// <summary>Badge colour key for StatusBrush.</summary>
    public string CategoryStatus { get; init; } = "";
    public bool IsProtected { get; init; }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private StartupType _startupType;
    [ObservableProperty] private int _processId;
    [ObservableProperty] private long _ramBytes = -1;
    [ObservableProperty] private bool _isSharedHost;
    [ObservableProperty] private bool _hasBackup;

    public string StatusText => IsRunning ? "Running" : "Stopped";

    public string WhatItDoes => string.IsNullOrEmpty(RuleLabel) ? Description : RuleLabel;

    public string StartupText => StartupType switch
    {
        StartupType.AutomaticDelayed => "Automatic (delayed)",
        StartupType.Automatic => "Automatic",
        StartupType.Manual => "Manual",
        StartupType.Disabled => "Disabled",
        StartupType.Boot => "Boot",
        StartupType.System => "System",
        _ => "Unknown"
    };

    public string RamText => IsSharedHost ? "Shared host" : RamBytes >= 0 ? Converters.Format.Bytes(RamBytes) : "—";

    /// <summary>Buttons are disabled for protected/critical services.</summary>
    public bool CanModify => !IsProtected;
    /// <summary>Per-action enablement: nothing to do if the service is already in that state.</summary>
    public bool CanStop => !IsProtected && IsRunning;
    public bool CanDisable => !IsProtected && StartupType != StartupType.Disabled;
    public bool CanSetManual => !IsProtected && StartupType != StartupType.Manual;

    partial void OnIsRunningChanged(bool value) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(CanStop)); }
    partial void OnStartupTypeChanged(StartupType value) { OnPropertyChanged(nameof(StartupText)); OnPropertyChanged(nameof(CanDisable)); OnPropertyChanged(nameof(CanSetManual)); }
    partial void OnRamBytesChanged(long value) => OnPropertyChanged(nameof(RamText));
    partial void OnIsSharedHostChanged(bool value) => OnPropertyChanged(nameof(RamText));
}
