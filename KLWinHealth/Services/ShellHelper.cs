using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>Explorer / URL / un-elevated launch helpers and Recycle Bin operations.</summary>
public static class ShellHelper
{
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warning(ex, "OpenFolder failed for {Path}", path); }
    }

    public static void ShowInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath) || Directory.Exists(filePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (dir != null && Directory.Exists(dir)) OpenFolder(dir);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "ShowInExplorer failed for {Path}", filePath); }
    }

    /// <summary>Opens a URL in the user's default browser, without admin rights.</summary>
    public static void OpenUrl(string url) => LaunchUnelevated(url, null);

    /// <summary>
    /// Starts a program/URL as the normal (non-elevated) user, by asking Explorer's desktop shell to do it
    /// (IShellDispatch2.ShellExecute). Browsers must never run elevated. Falls back to a normal launch.
    /// </summary>
    public static void LaunchUnelevated(string file, string? args)
    {
        if (!IsAdministrator() || !TryShellExecuteViaExplorer(file, args))
        {
            try { Process.Start(new ProcessStartInfo(file, args ?? "") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warning(ex, "Launch failed for {File}", file); }
        }
    }

    /// <summary>Moves a file or folder to the Recycle Bin (never a permanent delete).</summary>
    public static bool MoveToRecycleBin(string path, out string? error)
    {
        error = null;
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_NOERRORUI | Native.FOF_SILENT)
        };
        var rc = Native.SHFileOperation(ref op);
        if (rc != 0 || op.fAnyOperationsAborted)
        {
            error = rc == 0 ? "Cancelled." : $"Windows error 0x{rc:X}";
            return false;
        }
        return !File.Exists(path) && !Directory.Exists(path);
    }

    // ---------------------------------------------------------------- explorer COM plumbing
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(bool fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr pret);
        [return: MarshalAs(UnmanagedType.Interface)] IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(bool fEnterMode);
        void TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(bool fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IntPtr psvPrevious, IntPtr pfs, IntPtr psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetItemObject(uint uItem, ref Guid riid);
    }

    private static bool TryShellExecuteViaExplorer(string file, string? args)
    {
        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
            if (shellWindowsType == null) return false;
            dynamic shellWindows = Activator.CreateInstance(shellWindowsType)!;
            object loc = 0; // CSIDL_DESKTOP
            object root = Type.Missing;
            int hwnd;
            // SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1
            object? desktop = shellWindows.FindWindowSW(ref loc, ref root, 8, out hwnd, 1);
            if (desktop is not IServiceProvider sp) return false;

            var sidTopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var iidShellBrowser = typeof(IShellBrowser).GUID;
            if (sp.QueryService(ref sidTopLevelBrowser, ref iidShellBrowser, out var browserObj) != 0) return false;
            var browser = (IShellBrowser)browserObj;
            var view = browser.QueryActiveShellView();
            var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
            dynamic folderView = view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iidDispatch);
            dynamic shellDispatch = folderView.Application;
            shellDispatch.ShellExecute(file, args ?? "", "", "open", 1);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Un-elevated launch via Explorer failed");
            return false;
        }
    }
}
