using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>
/// Creates Task Scheduler tasks (via schtasks.exe + XML) for "Start with Windows" and the weekly auto-clean.
/// Tasks run with highest privileges, so there is no UAC prompt at login.
/// </summary>
public class SchedulerService
{
    public const string StartupTask = @"KLWinHealth\Startup";
    public const string AutoCleanTask = @"KLWinHealth\WeeklyClean";

    public async Task<bool> ExistsAsync(string taskName)
    {
        try
        {
            var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Query /TN \"{taskName}\"", timeout: TimeSpan.FromSeconds(15));
            return r.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "schtasks query failed");
            return false;
        }
    }

    public Task CreateStartupTaskAsync() =>
        CreateAsync(StartupTask, "Starts KL Win Health (minimized to tray) when you sign in.",
            $"""
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Esc(CurrentUser)}</UserId>
              <Delay>PT10S</Delay>
            </LogonTrigger>
            """, "--minimized");

    /// <summary>On-demand task (no trigger) that starts the installed app elevated; started by unelevated launches.</summary>
    public Task CreateLaunchTaskAsync() =>
        CreateAsync(Installer.LaunchTask, "Opens KL Win Health with administrator rights without a UAC prompt.",
            "", "", multipleInstances: "Parallel", priority: 4);

    public Task CreateAutoCleanTaskAsync(DayOfWeek day, TimeSpan time)
    {
        var start = DateTime.Today.Add(time);
        return CreateAsync(AutoCleanTask, "Weekly clean of Safe storage categories by KL Win Health.",
            $"""
            <CalendarTrigger>
              <StartBoundary>{start:yyyy-MM-ddTHH:mm:ss}</StartBoundary>
              <Enabled>true</Enabled>
              <ScheduleByWeek>
                <DaysOfWeek><{day} /></DaysOfWeek>
                <WeeksInterval>1</WeeksInterval>
              </ScheduleByWeek>
            </CalendarTrigger>
            """, "--autoclean");
    }

    public async Task DeleteAsync(string taskName)
    {
        if (!await ExistsAsync(taskName)) return;
        var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{taskName}\" /F", timeout: TimeSpan.FromSeconds(15));
        if (r.ExitCode != 0) throw new InvalidOperationException("Could not remove the scheduled task: " + (r.Error + r.Output).Trim());
        Log.Information("Scheduled task {Task} deleted", taskName);
    }

    private static string CurrentUser => WindowsIdentity.GetCurrent().Name;

    private static string Esc(string s) => SecurityElement.Escape(s) ?? s;

    private static async Task CreateAsync(string taskName, string description, string triggerXml, string args,
        string multipleInstances = "IgnoreNew", int priority = 7)
    {
#if KL_DEV_NOELEVATE
        var exe = AppPaths.ExePath;
#else
        var exe = Installer.TargetExe;
#endif
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>KL Win Health</Author>
                <Description>{Esc(description)}</Description>
              </RegistrationInfo>
            {(triggerXml.Length == 0 ? "" : "<Triggers>\n" + triggerXml + "\n</Triggers>")}
              <Principals>
                <Principal id="Author">
                  <UserId>{Esc(CurrentUser)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>{multipleInstances}</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>{priority}</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Esc(exe)}</Command>
                  <Arguments>{Esc(args)}</Arguments>
                  <WorkingDirectory>{Esc(Path.GetDirectoryName(exe) ?? "")}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"klwh_task_{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(tmp, xml, Encoding.Unicode);
            var r = await ProcessRunner.RunAsync("schtasks.exe", $"/Create /TN \"{taskName}\" /XML \"{tmp}\" /F", timeout: TimeSpan.FromSeconds(20));
            if (r.ExitCode != 0) throw new InvalidOperationException("Could not create the scheduled task: " + (r.Error + r.Output).Trim());
            Log.Information("Scheduled task {Task} created ({Args})", taskName, args);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
