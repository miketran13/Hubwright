using Hubwright.Auth;
using Hubwright.Engine;
using Hubwright.Journey;
using Hubwright.Security;
using Hubwright.Storage;

namespace Hubwright.Cli;

/// <summary>Stage 5, Secure: security findings and the security baseline (plan, check, preview, run, restore).</summary>
internal static partial class Commands
{
    internal static async Task<int> SecureAsync()
    {
        var session = Session();
        var sub = cli.At(1)?.ToLowerInvariant();
        var profile = Enum.TryParse<BaselineProfile>(cli.Option("profile"), true, out var p) ? p : BaselineProfile.Standard;
        switch (sub)
        {
            case null or "findings":
                return SecurityFindings(session);

            case "baseline":
                return Baseline(session, profile);

            case "login":
            {
                var config = HubwrightConfig.Load();
                config.Tenant.SignIn = Method() ?? config.Tenant.SignIn;
                var account = await TenantSignIn.ImplementAsync(config, null, null, CancellationToken.None);
                Console.WriteLine($"{account.Username} can apply the baseline: the app's write permissions are consented.");
                return 0;
            }

            case "restore":
            {
                var plan = SecurityBaselines.Restore(session, profile);
                var action = cli.At(2)?.ToLowerInvariant();
                return await PlanStepsAsync(session, plan, SecurityBaselines.RestoreFolder(session, profile), action, $"secure restore --profile {profile.ToString().ToLowerInvariant()}");
            }

            case "plan" or "check" or "preview" or "run" or "done" or "skip" or "rebuild":
            {
                var plan = SecurityBaselines.For(session, profile, rebuild: sub == "rebuild");
                return await PlanStepsAsync(session, plan, SecurityBaselines.Folder(session, profile), sub == "plan" ? null : sub, $"secure --profile {profile.ToString().ToLowerInvariant()}");
            }

            default:
                throw new InvalidOperationException($"Unknown: secure {sub}. Try secure, secure baseline, secure plan, check, preview, run, done, skip, rebuild or restore.");
        }
    }

    private static int SecurityFindings(HubwrightSession session)
    {
        var assessment = session.Workspace.RequireAssessment();
        var stage = JourneyStatus.For(session).First(s => s.Id == "secure");
        Console.WriteLine($"Secure: {stage.Status}");
        foreach (var c in assessment.Categories.Where(c => IntranetJourney.SecurityCategories.Contains(c.Category, StringComparer.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"  {c.Category,-18} grade {c.Grade,-2} {c.Failed} failed · {c.NotAssessed} not assessed");
        }

        Console.WriteLine();
        foreach (var f in assessment.Findings.Where(f => IntranetJourney.SecurityCategories.Contains(f.Category, StringComparer.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"{f.Severity,-8} {f.RuleId,-34} {f.Message}");
        }

        Console.WriteLine("\nThe security baseline: hubwright secure baseline [--profile standard|strict|regulated]");
        return 0;
    }

    private static int Baseline(HubwrightSession session, BaselineProfile profile)
    {
        var items = SecurityBaselines.Compare(session, profile);
        Console.WriteLine($"Security baseline, {profile} profile, against the scan of {session.Workspace.Snapshot!.CapturedAt:yyyy-MM-dd}:");
        foreach (var item in items)
        {
            var mark = item.State switch { BaselineState.Matches => "✓", BaselineState.Differs => "✗", _ => "?" };
            var how = item.Setting.Apply switch { BaselineApply.Graph => "graph", BaselineApply.Pnp => "pnp", _ => "guide" };
            Console.WriteLine($"  {mark} {how,-5} {item.Setting.Title,-46} {(item.Current is null ? "not read" : SecurityBaseline.Human(item.Current)),-28} → {SecurityBaseline.Human(item.Target)}");
        }

        Console.WriteLine($"\n{items.Count(i => i.State == BaselineState.Matches)} in place, {items.Count(i => i.State == BaselineState.Differs)} to change, {items.Count(i => i.State == BaselineState.NotRead)} not read (read again before anything changes; not a pass).");
        Console.WriteLine($"Next: hubwright secure plan --profile {profile.ToString().ToLowerInvariant()} · check · preview --all-automatic · run --steps <ids>");
        return 0;
    }
}
