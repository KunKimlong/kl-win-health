using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KLWinHealth.Services;
using Serilog;

namespace KLWinHealth.ViewModels;

/// <summary>Shared services every page needs (keeps constructors short).</summary>
public record PageContext(NavigationService Navigation, DialogService Dialogs, SettingsService Settings, BackupService Backups);

/// <summary>
/// Progress / status / cancel state for long operations. Bound to the BusyBar at the bottom of every module page.
/// </summary>
public partial class BusyState : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isIndeterminate = true;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _canCancel;

    public CancellationToken Begin(string status, bool cancellable = true)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        StatusText = status;
        IsIndeterminate = true;
        Progress = 0;
        CanCancel = cancellable;
        IsBusy = true;
        return _cts.Token;
    }

    /// <summary>Thread-safe progress update (0–100, or negative for indeterminate).</summary>
    public void Report(double percent, string? status = null)
    {
        void Apply()
        {
            if (percent < 0) IsIndeterminate = true;
            else { IsIndeterminate = false; Progress = Math.Clamp(percent, 0, 100); }
            if (status != null) StatusText = status;
        }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Apply(); else d.BeginInvoke(Apply);
    }

    public void End()
    {
        IsBusy = false;
        CanCancel = false;
        _cts?.Dispose();
        _cts = null;
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts == null) return;
        StatusText = "Cancelling…";
        CanCancel = false;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}

public abstract partial class PageViewModel : ObservableObject
{
    protected PageContext Ctx { get; }
    protected DialogService Dialogs => Ctx.Dialogs;
    protected SettingsService Settings => Ctx.Settings;
    protected BackupService Backups => Ctx.Backups;

    public abstract string Title { get; }
    public virtual string? Subtitle => null;

    public BusyState Busy { get; } = new();

    protected PageViewModel(PageContext ctx) => Ctx = ctx;

    /// <summary>Called each time the page is shown.</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>Called when leaving the page.</summary>
    public virtual void OnNavigatedFrom() { }

    [RelayCommand]
    private void GoBack() => Ctx.Navigation.GoHome();

    /// <summary>
    /// Runs a long operation with progress/cancel, never throwing to the UI: errors are logged and shown
    /// as a friendly message. Returns false if it failed or was cancelled.
    /// </summary>
    protected async Task<bool> RunBusyAsync(string status, Func<CancellationToken, Task> work, bool cancellable = true, string? errorTitle = null)
    {
        if (Busy.IsBusy) return false;
        var ct = Busy.Begin(status, cancellable);
        try
        {
            await work(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("{Page}: operation cancelled ({Status})", GetType().Name, status);
            Dialogs.Toast("Cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Page}: {Status} failed", GetType().Name, status);
            await Dialogs.ErrorAsync(errorTitle ?? "Something went wrong", FriendlyError(ex));
            return false;
        }
        finally
        {
            Busy.End();
        }
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access was denied. Some items are protected by Windows. Details were written to the log.",
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == 5 => "Access was denied by Windows. Make sure the app is running as administrator.",
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == 1060 => "That service doesn't exist anymore.",
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == 1062 => "That service isn't running.",
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == 1051 => "Other running services depend on this one, so it can't be stopped.",
        System.IO.IOException io when io.HResult == unchecked((int)0x80070020) => "The file is in use by another program.",
        InvalidOperationException { InnerException: System.ComponentModel.Win32Exception w } => FriendlyError(w),
        System.Net.Http.HttpRequestException => "Couldn't reach the server. Check your internet connection and try again.",
        _ => ex.Message
    };
}
