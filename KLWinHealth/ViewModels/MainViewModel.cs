using CommunityToolkit.Mvvm.ComponentModel;
using KLWinHealth.Services;

namespace KLWinHealth.ViewModels;

/// <summary>Root view model of the main window: current page + toast notifications.</summary>
public partial class MainViewModel : ObservableObject
{
    private CancellationTokenSource? _toastCts;

    public NavigationService Navigation { get; }

    [ObservableProperty] private string? _toastMessage;
    [ObservableProperty] private ToastKind _toastKind;
    [ObservableProperty] private bool _toastVisible;

    public MainViewModel(NavigationService navigation, DialogService dialogs)
    {
        Navigation = navigation;
        dialogs.ToastRequested += ShowToast;
    }

    private async void ShowToast(string message, ToastKind kind)
    {
        _toastCts?.Cancel();
        var cts = _toastCts = new CancellationTokenSource();
        ToastMessage = message;
        ToastKind = kind;
        ToastVisible = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(kind == ToastKind.Error ? 6 : 3.5), cts.Token);
            ToastVisible = false;
        }
        catch (TaskCanceledException) { }
    }
}
