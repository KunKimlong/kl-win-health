using System.Windows;
using KLWinHealth.Views;
using Microsoft.Win32;

namespace KLWinHealth.Services;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>Themed dialogs (no white MessageBoxes) plus non-blocking toasts.</summary>
public class DialogService
{
    public event Action<string, ToastKind>? ToastRequested;

    public void Toast(string message, ToastKind kind = ToastKind.Info) =>
        RunOnUi(() => ToastRequested?.Invoke(message, kind));

    public Task<bool> ConfirmAsync(string title, string message, IEnumerable<string>? details = null,
        string confirmText = "Continue", bool danger = false, string? warning = null) =>
        Show(danger ? DialogKind.Danger : DialogKind.Confirm, title, message, details, confirmText, "Cancel", warning);

    public Task InfoAsync(string title, string message, IEnumerable<string>? details = null) =>
        Show(DialogKind.Info, title, message, details, "OK", null, null);

    public Task SuccessAsync(string title, string message, IEnumerable<string>? details = null) =>
        Show(DialogKind.Success, title, message, details, "OK", null, null);

    public Task ErrorAsync(string title, string message, IEnumerable<string>? details = null) =>
        Show(DialogKind.Error, title, message, details, "OK", null, null);

    private static Task<bool> Show(DialogKind kind, string title, string message, IEnumerable<string>? details,
        string ok, string? cancel, string? warning)
    {
        var tcs = new TaskCompletionSource<bool>();
        RunOnUi(() =>
        {
            try
            {
                var dlg = new DialogWindow(kind, title, message, details, ok, cancel, warning);
                var owner = Application.Current?.MainWindow;
                if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized)
                    dlg.Owner = owner;
                else
                    dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                tcs.TrySetResult(dlg.ShowDialog() == true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Dialog failed");
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
    }

    public string? PickFile(string title, string filter, bool multi = false) =>
        PickFiles(title, filter, multi)?.FirstOrDefault();

    public string[]? PickFiles(string title, string filter, bool multi = true)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multi, CheckFileExists = true };
        return dlg.ShowDialog(Application.Current?.MainWindow) == true ? dlg.FileNames : null;
    }

    public string? PickFolder(string title)
    {
        var dlg = new OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog(Application.Current?.MainWindow) == true ? dlg.FolderName : null;
    }

    public string? SaveFile(string title, string filter, string fileName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, OverwritePrompt = true };
        return dlg.ShowDialog(Application.Current?.MainWindow) == true ? dlg.FileName : null;
    }

    private static void RunOnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }
}
