using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Converters;
using KLWinHealth.Services;

namespace KLWinHealth.ViewModels;

public partial class HomeViewModel : PageViewModel
{
    private readonly SystemStatsService _stats;

    public override string Title => "Home";

    public string Greeting => "Welcome back, Long";

    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _ramSub = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _diskText = "—";
    [ObservableProperty] private string _diskSub = "";
    [ObservableProperty] private double _diskUsedPercent;
    [ObservableProperty] private int _threats;
    [ObservableProperty] private string _threatsSub = "No scan yet";

    public bool HasThreats => Threats > 0;
    public string StatusText => HasThreats ? "System status: threats found" : "System status: protected";

    partial void OnThreatsChanged(int value)
    {
        OnPropertyChanged(nameof(HasThreats));
        OnPropertyChanged(nameof(StatusText));
    }

    public HomeViewModel(PageContext ctx, SystemStatsService stats) : base(ctx)
    {
        _stats = stats;
        _stats.PropertyChanged += OnStatsChanged;
        Settings.Saved += (_, _) => RefreshThreats();
        RefreshThreats();
        UpdateAll();
    }

    public override Task OnNavigatedToAsync()
    {
        RefreshThreats();
        return Task.CompletedTask;
    }

    private void RefreshThreats()
    {
        var s = Settings.Current;
        Threats = s.LastScanThreats;
        ThreatsSub = s.LastScanTime is { } t ? $"Last scan {t:MMM d, HH:mm}" : "No scan yet";
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => UpdateAll();

    private void UpdateAll()
    {
        RamPercent = _stats.RamPercent;
        RamText = $"{_stats.RamPercent:0}%";
        RamSub = _stats.RamTotal > 0 ? $"{Format.Bytes((long)_stats.RamUsed)} / {Format.Bytes((long)_stats.RamTotal)}" : "";
        CpuPercent = _stats.CpuPercent;
        CpuText = $"{_stats.CpuPercent:0}%";
        if (_stats.SystemDriveTotal > 0)
        {
            DiskText = Format.Bytes(_stats.SystemDriveFree);
            DiskSub = $"free of {Format.Bytes(_stats.SystemDriveTotal)}";
            DiskUsedPercent = 100.0 * (_stats.SystemDriveTotal - _stats.SystemDriveFree) / _stats.SystemDriveTotal;
        }
    }

    [RelayCommand]
    private void Open(string page)
    {
        var type = page switch
        {
            "Virus" => typeof(VirusCheckViewModel),
            "Rog" => typeof(RogServicesViewModel),
            "Windows" => typeof(WindowsServicesViewModel),
            "Browser" => typeof(BrowserCleanerViewModel),
            "Storage" => typeof(StorageViewModel),
            "Wallpaper" => typeof(WallpaperViewModel),
            "Widgets" => typeof(WidgetsViewModel),
            "Settings" => typeof(SettingsViewModel),
            _ => null
        };
        if (type != null) Ctx.Navigation.NavigateTo(type);
    }
}
