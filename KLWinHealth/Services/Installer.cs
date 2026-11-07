using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>
/// One-time install so the app only asks for administrator rights once.
///
/// The exe manifest is asInvoker. On the first elevated run (one UAC prompt) the exe copies itself to
/// %ProgramFiles%\KL Win Health\ (writable only by administrators), registers an on-demand
/// "KLWinHealth\Launch" task with highest privileges, and creates Start menu / Desktop shortcuts.
/// Later launches run unelevated, start that task (no UAC prompt) and exit; the task starts the
/// installed copy elevated.
/// </summary>
public static class Installer
{
    public const string LaunchTask = @"KLWinHealth\Launch";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KLWinHealth";
    private const string DisplayName = "KL Win Health";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DisplayName);
    public static string InstalledExe { get; } = Path.Combine(InstallDir, "KLWinHealth.exe");

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), DisplayName + ".lnk");
    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DisplayName + ".lnk");

    public static bool IsInstalled => File.Exists(InstalledExe);

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Exe that scheduled tasks should point at: the installed copy when there is one.</summary>
    public static string TargetExe => IsInstalled ? InstalledExe : AppPaths.ExePath;

    /// <summary>True when the running exe is the same build as the installed copy (File.Copy keeps the timestamp).</summary>
    private static bool InstalledCopyIsCurrent()
    {
        if (IsRunningInstalled) return true;
        try
        {
            var me = new FileInfo(AppPaths.ExePath);
            var inst = new FileInfo(InstalledExe);
            return inst.Exists && me.Length == inst.Length && me.LastWriteTimeUtc == inst.LastWriteTimeUtc;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ unelevated side

    /// <summary>
    /// Called when not elevated. Starts the elevated app (via the task, or one UAC prompt to install/update)
    /// and returns true if this process should exit. Returns false if the user cancelled UAC.
    /// </summary>
    public static async Task<bool> HandOffAsync(IEnumerable<string> args, bool forceUac)
    {
        if (!forceUac && IsInstalled && InstalledCopyIsCurrent())
        {
            try
            {
                var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Run /TN \"{LaunchTask}\"", timeout: TimeSpan.FromSeconds(15));
                if (r.ExitCode == 0)
                {
                    Log.Information("Started elevated copy via the {Task} task", LaunchTask);
                    return true;
                }
                Log.Warning("schtasks /Run failed ({Code}): {Msg}", r.ExitCode, (r.Error + r.Output).Trim());
            }
            catch (Exception ex) { Log.Warning(ex, "schtasks /Run failed"); }
        }

        // First run, update, or broken task: one UAC prompt. The elevated copy installs/repairs itself.
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExePath, string.Join(' ', args.Select(Quote)))
            { UseShellExecute = true, Verb = "runas" });
            Log.Information("Relaunched elevated (UAC) to install/update");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Warning("UAC prompt cancelled by the user");
            return false;
        }
    }

    // ------------------------------------------------------------------ elevated side

    /// <summary>
    /// Elevated: copies this exe to Program Files (if needed), registers the launch task, shortcuts and the
    /// Apps &amp; features entry. Returns true when the installed copy is ready.
    /// </summary>
    public static async Task<bool> InstallAsync(SchedulerService scheduler, SettingsService settings)
    {
        try
        {
            if (!InstalledCopyIsCurrent())
            {
                Directory.CreateDirectory(InstallDir);
                var tmp = InstalledExe + ".new";
                File.Copy(AppPaths.ExePath, tmp, overwrite: true);
                File.Move(tmp, InstalledExe, overwrite: true);
                Log.Information("Installed {Src} to {Dst}", AppPaths.ExePath, InstalledExe);
            }

            await scheduler.CreateLaunchTaskAsync();

            // Re-point existing tasks at the installed exe.
            if (await scheduler.ExistsAsync(SchedulerService.StartupTask))
                await scheduler.CreateStartupTaskAsync();
            var s = settings.Current;
            if (s.AutoCleanEnabled && await scheduler.ExistsAsync(SchedulerService.AutoCleanTask)
                && TimeSpan.TryParse(s.AutoCleanTime, out var t))
                await scheduler.CreateAutoCleanTaskAsync(s.AutoCleanDay, t);

            CreateShortcut(StartMenuShortcut, InstalledExe, "", "Open KL Win Health");
            CreateShortcut(DesktopShortcut, InstalledExe, "", "Open KL Win Health");
            RegisterUninstallEntry();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Install failed; continuing from {Exe}", AppPaths.ExePath);
            return false;
        }
    }

    /// <summary>Elevated, from the installed copy: makes sure the launch task still exists (cheap, background).</summary>
    public static async Task EnsureLaunchTaskAsync(SchedulerService scheduler)
    {
        try
        {
            if (!await scheduler.ExistsAsync(LaunchTask)) await scheduler.CreateLaunchTaskAsync();
        }
        catch (Exception ex) { Log.Warning(ex, "Could not repair the launch task"); }
    }

    /// <summary>
    /// Elevated, from the installed copy: "Start with Windows" is on unless the user turned it off, so desktop widgets
    /// and the wallpaper slideshow come back after a restart without opening the app by hand. Also self-heals a
    /// startup task that was deleted outside the app.
    /// </summary>
    public static async Task EnsureStartupTaskAsync(SchedulerService scheduler, SettingsService settings)
    {
        try
        {
            if (settings.Current.StartWithWindows == false) return;
            if (!await scheduler.ExistsAsync(SchedulerService.StartupTask))
            {
                await scheduler.CreateStartupTaskAsync();
                Log.Information("Start with Windows enabled (default / repaired)");
            }
            if (settings.Current.StartWithWindows != true)
            {
                settings.Current.StartWithWindows = true;
                settings.Save();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Could not register the startup task"); }
    }

    /// <summary>Elevated: removes tasks, shortcuts, the Apps &amp; features entry and the Program Files folder.
    /// Data in %ProgramData%\KLWinHealth (backups, quarantine, settings) is kept.</summary>
    public static async Task UninstallAsync(SchedulerService scheduler)
    {
        foreach (var task in new[] { LaunchTask, SchedulerService.StartupTask, SchedulerService.AutoCleanTask })
        {
            try { await scheduler.DeleteAsync(task); } catch (Exception ex) { Log.Warning(ex, "Removing task {Task} failed", task); }
        }
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
        {
            try { if (File.Exists(lnk)) File.Delete(lnk); } catch (Exception ex) { Log.Warning(ex, "Removing {Lnk} failed", lnk); }
        }
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch (Exception ex) { Log.Warning(ex, "Removing uninstall entry failed"); }

        // The exe may be the one running: delete the folder a few seconds after we exit.
        if (Directory.Exists(InstallDir))
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & rd /s /q \"{InstallDir}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        Log.Information("Uninstalled");
    }

    // ------------------------------------------------------------------ helpers

    private static void RegisterUninstallEntry()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", DisplayName);
        k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        k.SetValue("Publisher", "KL");
        k.SetValue("DisplayIcon", InstalledExe);
        k.SetValue("InstallLocation", InstallDir);
        k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); } catch { }
    }

    private static void CreateShortcut(string lnkPath, string target, string args, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic lnk = shell.CreateShortcut(lnkPath);
            lnk.TargetPath = target;
            lnk.Arguments = args;
            lnk.WorkingDirectory = Path.GetDirectoryName(target);
            lnk.IconLocation = target + ",0";
            lnk.Description = description;
            lnk.Save();
        }
        catch (Exception ex) { Log.Warning(ex, "Creating shortcut {Lnk} failed", lnkPath); }
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
