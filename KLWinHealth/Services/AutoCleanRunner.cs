using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace KLWinHealth.Services;

/// <summary>
/// Headless weekly auto-clean (KLWinHealth.exe --autoclean, started by Task Scheduler).
/// Cleans ONLY categories marked "Safe" in cleanup_categories.json. No UI.
/// </summary>
public static class AutoCleanRunner
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        using var mutex = new Mutex(true, @"Global\KLWinHealth.AutoClean.v1", out var createdNew);
        if (!createdNew)
        {
            Log.Information("Auto-clean already running; exiting");
            return 0;
        }
        try
        {
            var storage = services.GetRequiredService<StorageService>();
            var total = CleanResult.Empty;
            using var cts = new CancellationTokenSource(TimeSpan.FromHours(1));
            foreach (var c in storage.CreateCategories().Where(c => !c.IsCaution && !c.IsShortcut))
            {
                try { total = total.Add(await storage.CleanAsync(c, null, cts.Token)); }
                catch (Exception ex) { Log.Error(ex, "Auto-clean: category {Name} failed", c.Name); }
            }
            Log.Information("Auto-clean finished: freed {Bytes} bytes, {Deleted} files deleted, {Skipped} skipped",
                total.BytesFreed, total.FilesDeleted, total.FilesSkipped);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Auto-clean failed");
            return 1;
        }
    }
}
