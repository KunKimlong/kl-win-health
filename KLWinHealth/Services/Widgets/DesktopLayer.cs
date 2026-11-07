using System.Runtime.InteropServices;
using Serilog;

namespace KLWinHealth.Services.Widgets;

/// <summary>
/// Finds the desktop window layer (behind normal windows, survives Win+D) and attaches widget windows to it.
///
/// Explorer creates a "WorkerW" window behind the desktop icons when Progman receives the undocumented 0x052C message.
///  • Classic layout (Windows 10 / 11 before 24H2): SHELLDLL_DefView lives in a top-level WorkerW; another WorkerW is behind it.
///  • 24H2+ layout: SHELLDLL_DefView is a child of Progman, and the WorkerW is a child of Progman too.
///
/// Widgets stay top-level layered windows (so per-pixel transparency keeps working) and are OWNED by the window that hosts
/// the desktop icons. Owned windows always stay just above their owner, so they sit on the desktop, below every app window,
/// and are not hidden by "Show desktop".
/// </summary>
public static class DesktopLayer
{
    private const uint SpawnWorkerW = 0x052C;
    private const uint SMTO_NORMAL = 0x0000;
    private const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    public enum Layout { Unknown, Classic, Modern24H2 }

    public record DesktopInfo(IntPtr Progman, IntPtr IconHost, IntPtr WorkerW, Layout Layout);

    /// <summary>Asks Explorer to create the WorkerW and returns the desktop windows (zeros if Explorer isn't running).</summary>
    public static DesktopInfo Find()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return new DesktopInfo(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Layout.Unknown);

        Native.SendMessageTimeout(progman, SpawnWorkerW, new IntPtr(0xD), new IntPtr(0x1), SMTO_NORMAL, 1000, out _);
        Native.SendMessageTimeout(progman, SpawnWorkerW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);

        // 24H2+: icons (SHELLDLL_DefView) and the WorkerW are both children of Progman.
        var defViewInProgman = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        var workerInProgman = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (defViewInProgman != IntPtr.Zero && workerInProgman != IntPtr.Zero)
            return new DesktopInfo(progman, progman, workerInProgman, Layout.Modern24H2);

        // Classic: find the top-level window that hosts SHELLDLL_DefView; the WorkerW right after it is behind the icons.
        IntPtr iconHost = IntPtr.Zero, worker = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                iconHost = h;
                worker = Native.FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
                return false;
            }
            return true;
        }, IntPtr.Zero);
        if (iconHost != IntPtr.Zero)
            return new DesktopInfo(progman, iconHost, worker, Layout.Classic);

        // Icons hidden / unusual shell: fall back to Progman itself.
        return new DesktopInfo(progman, progman, workerInProgman, defViewInProgman != IntPtr.Zero ? Layout.Modern24H2 : Layout.Unknown);
    }

    /// <summary>Attaches a widget window to the desktop layer (owner = icon host, bottom of the z-order).</summary>
    public static bool Attach(IntPtr widget, DesktopInfo d)
    {
        if (widget == IntPtr.Zero) return false;
        var owner = d.IconHost != IntPtr.Zero ? d.IconHost : d.Progman;
        if (owner == IntPtr.Zero || !IsWindow(owner))
        {
            Log.Warning("Desktop layer not found; widget stays at the bottom of the normal z-order");
            PushToBottom(widget);
            return false;
        }
        SetWindowLongPtr(widget, GWLP_HWNDPARENT, owner);
        PushToBottom(widget);
        return true;
    }

    /// <summary>Detaches a widget for edit mode (no owner, on top so it can be dragged).</summary>
    public static void Detach(IntPtr widget)
    {
        if (widget == IntPtr.Zero) return;
        SetWindowLongPtr(widget, GWLP_HWNDPARENT, IntPtr.Zero);
    }

    public static bool IsAttached(IntPtr widget, DesktopInfo d) =>
        widget != IntPtr.Zero && GetWindowLongPtr(widget, GWLP_HWNDPARENT) is var o && o != IntPtr.Zero && (o == d.IconHost || o == d.Progman) && IsWindow(o);

    public static void PushToBottom(IntPtr widget) =>
        Native.SetWindowPos(widget, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

    /// <summary>Sets/clears click-through (WS_EX_TRANSPARENT) and keeps the window out of Alt+Tab and the taskbar.</summary>
    public static void SetClickThrough(IntPtr widget, bool clickThrough)
    {
        var ex = Native.GetWindowLongPtr(widget, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
        ex &= ~Native.WS_EX_APPWINDOW;
        if (clickThrough) ex |= Native.WS_EX_TRANSPARENT; else ex &= ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtr(widget, Native.GWL_EXSTYLE, new IntPtr(ex));
        Native.SetWindowPos(widget, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }
}
