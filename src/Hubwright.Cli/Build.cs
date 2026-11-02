using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hubwright.Agents;
using Hubwright.Auth;
using Hubwright.Blueprints;
using Hubwright.Chat;
using Hubwright.Collectors;
using Hubwright.Config;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Journey;
using Hubwright.Mcp;
using Hubwright.Preview;
using Hubwright.Proposals;
using Hubwright.Reports;
using Hubwright.Rules;
using Hubwright.Skills;
using Hubwright.Snapshot;
using Hubwright.Storage;
using Hubwright.Theming;

namespace Hubwright.Cli;

/// <summary>Stage 4, Build: the build plan, previews, checks and runs.</summary>
internal static partial class Commands
{
    /// <summary>
    /// Lists the automatic steps (with what they depend on) that will change the tenant, asks for the
    /// tenant name unless --yes, then runs them. The one way the CLI changes a tenant.
    /// </summary>
    private static async Task<int> ConfirmAndRunAsync(HubwrightSession session, Hubwright.Implementation.ImplementationPlan plan, string folder, IReadOnlyList<string> ids, string? warning = null)
    {
        var steps = plan.WithDependencies(ids).Where(s => s.Kind == Hubwright.Implementation.StepKind.Automatic && !s.IsFinished).ToList();
        if (steps.Count == 0)
        {
            Console.WriteLine("Nothing to run: those steps are finished.");
            return 0;
        }

        Console.WriteLine($"{steps.Count} automatic step(s) will change {plan.RootUrl}:");
        foreach (var s in steps)
        {
            Console.WriteLine($"  {s.Risk,-8} {s.Title}");
        }

        if (warning is not null)
        {
            Console.WriteLine(warning);
        }

        if (!cli.Flag("yes"))
        {
            if (Console.IsInputRedirected)
            {
                throw new InvalidOperationException("Pass --yes to run without a terminal.");
            }

            Console.Write($"Type the tenant name ({session.Config.Tenant.Name}) to continue: ");
            if (!string.Equals(Console.ReadLine()?.Trim(), session.Config.Tenant.Name, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelled. Nothing was changed.");
                return 1;
            }
        }

        var runner = Implementations.Runner(session, plan, folder);
        var progress = new Progress<StepEvent>(e => Console.WriteLine($"  {e.Step.Status,-11} {e.Step.Id}: {e.Message}"));
        await runner.RunAsync(ids, progress, CancellationToken.None);
        return plan.Steps.Any(s => s.Status == Hubwright.Implementation.StepStatus.Failed) ? 1 : 0;
    }

    internal static async Task<int> BuildAsync()
    {
        var session = Session();
        var sub = cli.At(1)?.ToLowerInvariant();

        if (sub == "login")
        {
            var config = HubwrightConfig.Load();
            config.Tenant.SignIn = Method() ?? config.Tenant.SignIn;
            var account = await TenantSignIn.ImplementAsync(config, null, null, CancellationToken.None);
            Console.WriteLine($"{account.Username} can build: the app's write permissions are consented.");
            return 0;
        }

        var folders = session.Store.IntranetProposals();
        var index = int.TryParse(sub is "run" or "preview" or "done" or "skip" or "rebuild" or "check" ? cli.Option("proposal") : cli.At(1), out var n) ? n - 1 : 0;
        var folder = index >= 0 && index < folders.Count ? folders[index] : throw new InvalidOperationException("No proposal yet: run `hubwright proposal new` first.");
        var plan = sub == "rebuild" ? Implementations.Rebuild(session, folder) : Implementations.For(session, folder);
        return await PlanStepsAsync(session, plan, folder, sub, "build");
    }

    /// <summary>
    /// What `build` and `secure` share: list the steps, check them read-only, preview the exact writes,
    /// run the selected automatic steps after the typed tenant name, and record guided steps.
    /// </summary>
    internal static async Task<int> PlanStepsAsync(HubwrightSession session, Hubwright.Implementation.ImplementationPlan plan, string folder, string? sub, string verb)
    {
        IEnumerable<string> Selected()
        {
            if (cli.Flag("all-automatic"))
            {
                return plan.Steps.Where(s => s.Kind == Hubwright.Implementation.StepKind.Automatic && !s.IsFinished).Select(s => s.Id);
            }

            var ids = (cli.Option("steps") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var id in ids.Where(i => plan.Find(i) is null))
            {
                throw new InvalidOperationException($"No step {id}. Run `hubwright {verb}` to see the steps.");
            }

            return ids.Count > 0 ? ids : throw new InvalidOperationException("Choose steps: --steps id,id or --all-automatic.");
        }

        switch (sub)
        {
            case "rebuild" or "check":
            {
                if (sub == "rebuild")
                {
                    Console.WriteLine($"Plan rebuilt: {plan.Steps.Count(s => s.IsFinished)} unchanged finished step(s) kept.");
                }

                if (session.Workspace.Snapshot?.Source is SnapshotSource.Sample or SnapshotSource.Blueprint)
                {
                    Console.WriteLine("Sample or design-only data: there is no tenant to check.");
                    return 0;
                }

                var pending = plan.Steps.Count(s => !s.IsFinished);
                var progress = new Progress<StepEvent>(e => Console.WriteLine($"  {e.Step.Status,-11} {e.Step.Id}: {e.Message}"));
                var found = await Implementations.Checker(session, plan, folder).CheckAsync(plan.Steps.Select(s => s.Id), progress, CancellationToken.None);
                Console.WriteLine($"Checked the tenant: {found} of {pending} step(s) already in place. Nothing was changed.");
                return 0;
            }

            case "preview":
            {
                foreach (var (step, request) in Implementations.Preview(plan, Selected()))
                {
                    Console.WriteLine($"{step.Id,-34} {request.Method}{(request.Merge ? " (MERGE)" : ""),-6} {request.Url}");
                    if (request.Body is { } body)
                    {
                        Console.WriteLine($"{"",-34} {(body.Length > 160 ? body[..160] + "…" : body)}");
                    }
                }

                if (cli.Option("out") is { } dir)
                {
                    Directory.CreateDirectory(dir);
                    foreach (var (step, request) in Implementations.Preview(plan, Selected()).Where(x => x.Request.Method == "PnP"))
                    {
                        File.WriteAllText(Path.Combine(dir, step.Id + ".ps1"), request.Body);
                    }

                    Console.WriteLine($"PnP scripts written to {Path.GetFullPath(dir)} (they read the token from $env:{Hubwright.Implementation.PnpScripts.TokenVariable}).");
                }

                Console.WriteLine("Nothing was sent. Values in {braces} are filled in from earlier steps when they run.");
                return 0;
            }

            case "run":
            {
                if (session.Workspace.Snapshot?.Source is SnapshotSource.Sample or SnapshotSource.Blueprint)
                {
                    throw new InvalidOperationException("This tenant is sample or design-only data: preview the plan, but scan a real tenant before running it.");
                }

                var code = await ConfirmAndRunAsync(session, plan, folder, [.. Selected()]);
                Console.WriteLine($"{plan.Steps.Count(s => s.IsFinished)} of {plan.Steps.Count} steps finished. Guided steps: hubwright {verb} done <step> after you do them.");
                return code;
            }

            case "done":
            case "skip":
                Implementations.Mark(session, plan, folder, cli.At(2) ?? throw new InvalidOperationException("Which step?"), sub == "done");
                Console.WriteLine($"{cli.At(2)}: {(sub == "done" ? "done" : "skipped")}.");
                return 0;

            default:
            {
                Console.WriteLine($"{plan.Proposal} · {plan.RootUrl}");
                string? phase = null;
                foreach (var s in plan.Steps)
                {
                    if (s.Phase != phase)
                    {
                        phase = s.Phase;
                        Console.WriteLine($"\n{phase}");
                    }

                    Console.WriteLine($"  {s.Status,-11} {(s.Kind == Hubwright.Implementation.StepKind.Automatic ? Hubwright.Implementation.PnpScripts.IsPnp(s.Operation) ? "pnp  " : "auto " : "guide"),-5} {s.Risk,-8} {s.Id,-34} {s.Title}{(s.Target is null ? "" : "  " + s.Target)}");
                }

                Console.WriteLine($"\nPreview: hubwright {verb} preview --all-automatic · Run: hubwright {verb} run --steps <ids>");
                return 0;
            }
        }
    }
}
