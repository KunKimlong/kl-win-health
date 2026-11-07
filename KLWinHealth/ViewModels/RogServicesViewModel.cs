using KLWinHealth.Models;
using KLWinHealth.Services;

namespace KLWinHealth.ViewModels;

/// <summary>Module 2 — ASUS / ROG services. Rules: config\rog_services.json.</summary>
public class RogServicesViewModel : ServicesPageViewModel
{
    public override string Title => "Clean ROG service";
    protected override string Module => Modules.RogServices;
    public override string ProtectedLabel => "Required";
    public override MahApps.Metro.IconPacks.PackIconMaterialKind PageIcon => MahApps.Metro.IconPacks.PackIconMaterialKind.GamepadVariant;
    public override string OtherLabel => "Unknown — not recommended";
    public override string Subtitle => "ASUS / ROG services. Required ones (fan & performance modes, Fn keys & backlight, battery limit, GPU switching) are locked.";

    public RogServicesViewModel(PageContext ctx, ServiceManager manager, ConfigService config) : base(ctx, manager, config) { }

    protected override async Task<List<ServiceItem>> LoadItemsAsync(CancellationToken ct)
    {
        var rules = Config.RogServices;
        var raw = await Manager.QueryAsync(ct);
        return await Task.Run(() =>
        {
            // ASUS/ROG detection: name / display name patterns, or the publisher of the service executable.
            var asus = raw.Where(r =>
                rules.Find(r.Name, r.DisplayName) != null ||
                rules.DetectPatterns.Any(p => Wildcard.IsMatch(r.Name, p) || Wildcard.IsMatch(r.DisplayName, p)) ||
                (ServiceManager.GetPublisher(ServiceManager.ExtractExe(r.PathName)) is { } pub &&
                 rules.DetectPublishers.Any(d => pub.Contains(d, StringComparison.OrdinalIgnoreCase))));

            return Manager.BuildItems(asus, r =>
            {
                var rule = rules.Find(r.Name, r.DisplayName);
                var cat = rule?.Category ?? RuleCategory.Unknown;
                return cat switch
                {
                    RuleCategory.Protected => (cat, rule, "Required", "Required"),
                    RuleCategory.Safe => (cat, rule, "Safe to stop", "Safe"),
                    RuleCategory.Caution => (cat, rule, "Caution", "Caution"),
                    _ => (cat, rule, "Unknown — not recommended", "Unknown")
                };
            });
        }, ct);
    }
}

/// <summary>Module 3 — all Windows services. Rules: config\windows_services.json.</summary>
public class WindowsServicesViewModel : ServicesPageViewModel
{
    public override string Title => "Clean Windows service";
    protected override string Module => Modules.WindowsServices;
    public override string ProtectedLabel => "Critical";
    public override MahApps.Metro.IconPacks.PackIconMaterialKind PageIcon => MahApps.Metro.IconPacks.PackIconMaterialKind.MicrosoftWindows;
    public override string OtherLabel => "Not classified";
    public override string Subtitle => "Stop services you don't use. Critical services are locked — Windows Defender always stays on.";

    public WindowsServicesViewModel(PageContext ctx, ServiceManager manager, ConfigService config) : base(ctx, manager, config) { }

    protected override async Task<List<ServiceItem>> LoadItemsAsync(CancellationToken ct)
    {
        var rules = Config.WindowsServices;
        var rogRules = Config.RogServices;
        var raw = await Manager.QueryAsync(ct);
        return await Task.Run(() => Manager.BuildItems(raw, r =>
        {
            // Windows rules first; then the ROG rules, so ASUS services required for fans / Fn keys /
            // battery limit / GPU switching are locked on this page too.
            // (Only ROG *Protected* rules apply here; ROG Safe items are handled on the ROG page.)
            var rule = rules.Find(r.Name, r.DisplayName);
            if (rule == null && rogRules.Find(r.Name, r.DisplayName) is { Category: RuleCategory.Protected } rogRule) rule = rogRule;
            var cat = rule?.Category ?? RuleCategory.Unknown;
            if (ServiceControl.IsHardProtected(r.Name)) cat = RuleCategory.Protected;
            return cat switch
            {
                RuleCategory.Protected => (cat, rule, "Critical", "Critical"),
                RuleCategory.Safe => (cat, rule, "Safe to stop", "Safe"),
                RuleCategory.Caution => (cat, rule, "Caution", "Caution"),
                _ => (cat, rule, "Not classified", "Other")
            };
        }), ct);
    }
}
