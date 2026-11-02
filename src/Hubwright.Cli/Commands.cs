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

/// <summary>The CLI: dispatch, shared state and the commands that belong to no stage (help, doctor, tenants, audit).</summary>
internal static partial class Commands
{
    // Set once by Program.cs from the command line.
    internal static Args cli = new([]);
    internal static string? command;
    internal static string? tenant;
    internal static string? self;

    internal static async Task<int> RunAsync()
    {
        try
        {
            return command switch
            {
                null or "help" or "-h" => Help(),
                "doctor" => Doctor(),
                "sample" => Sample(),
                "connect" => Connect(),
                "login" => await LoginAsync(),
                "logout" => await LogoutAsync(),
                "scan" => await ScanAsync(),
                "import" => Import(),
                "assess" or "report" => await AssessAsync(),
                "findings" => Findings(),
                "access" => Access(),
                "blueprint" => await BlueprintAsync(),
                "proposal" => await ProposalAsync(),
                "build" => await BuildAsync(),
                "setup" => await SetupAsync(),
                "status" => Status(),
                "secure" => await SecureAsync(),
                "drift" => DriftCommand(),
                "trend" => Trend(),
                "owners" => Owners(),
                "summary" => SummaryCommand(),
                "schedule" => await ScheduleAsync(),
                "templates" => Templates(),
                "design" => await DesignAsync(),
                "scope" => Scope(),
                "theme" => Theme(),
                "preview" => Preview(),
                "diff" => Diff(),
                "facts" => Facts(),
                "skills" => await SkillsAsync(),
                "proposals" => Proposals(),
                "ask" => await AskAsync(),
                "chat" => await ChatAsync(),
                "mcp" => await McpAsync(),
                "audit" => Audit(),
                "tenants" => Tenants(),
                _ => Unknown(),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException or IOException or SignInRequiredException or ConnectorException or RuleException or TimeoutException or JsonException or UnauthorizedAccessException or Microsoft.Identity.Client.MsalException or HttpRequestException)
        {
            Console.Error.WriteLine($"hubwright: {ex.Message}");
            return 1;
        }
    }

    internal static HubwrightSession Session() => HubwrightSession.Open(tenant, self);

    internal static int Unknown()
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run `hubwright help`.");
        return 2;
    }

    internal static int Help()
    {
        Console.WriteLine("""
            🏗️ Hubwright: plan, build, secure and run your SharePoint intranet.

            Journey
              scope [--read]                    what Hubwright reads and changes, with which permissions (--read: record you've read it)
              status                            where you are: Connect → Assess → Plan → Build → Secure → Run, and the next step
              setup [--install]                 check (and install) what Hubwright needs: app, sign-in, PnP PowerShell
              secure                            sharing, permission and Copilot findings and their grades
              secure baseline [--profile standard|strict|regulated]   the tenant against the security baseline
              secure plan|check|preview|run|done|skip|rebuild [--profile p] [--steps a,b | --all-automatic] [--yes]
              secure restore [preview|run] [--profile p]   put back the values the baseline replaced
              secure login                      consent to the app's write permissions

            Run
              drift [--csv f]                   the tenant against the plan and the baseline: what drifted since it was built
              trend                             the grade of every scan, oldest first
              owners [--csv f]                  sites below the minimum number of owners (an attestation list for Excel)
              summary [--out f]                 a short status to paste into Teams or an email
              schedule weekly|daily|off|status  scheduled read-only scans (Windows Task Scheduler, as you, never prompts)

            Start
              sample [fabrikam|northwind]       load a made-up tenant to explore (no sign-in)
              blueprint new [--answers f.json]  design an intranet from scratch (questionnaire)
              connect --client-id <app id> [--tenant-id <id>] [--tenant contoso] [--method browser|windows|device]
              login [--method browser|windows|device]   sign in as a SharePoint admin (read-only, delegated);
                                                the browser is the default, and the tenant is found from Graph
              logout
              scan [--scope <hub or site url>] [--no-prompt]   read the tenant into a snapshot (--no-prompt: fail instead of asking to sign in)
              import <snapshot.json>            use a snapshot from tools/collect-snapshot.ps1

            Assess
              assess [--format md|html|json] [--out file] [--ai] [--open]
              findings [--severity high] [--category hubs] [--csv f]
              access [--csv f]                  the sites the scan could not open (not assessed) and how to see or get access
              diff [<old.json> <new.json>]      what changed between snapshots (default: latest two)
              facts [tenant|site|hub]           the facts rule expressions can use

            Intranet proposal (after a scan)
              proposal new [--answers a.json] [--approach keep|balanced|restructure] [--ai] [--open]
                                                asks questions seeded from the scan, then writes the proposal
              proposal answers [--out a.json]   the seeded answers, to edit and pass to proposal new
              proposal list | show [n] [--format html|md] [--open] | use [n]   (use = make it the blueprint)
              proposal structure [n] [--open]   the interactive structure preview (today, target, moves)
              proposal sites [n] [--open]       walk through every proposed site with sample content
              templates [list] [--source microsoft|package] · templates suggest "<title>" [--purpose "..."]
              templates gallery [--open]        every built-in template and look book package, sketched
              design [show|preview "<site>" [--open]]   each site's design: purpose, pattern, home page, navigation, why
              design patterns · design pattern "<site>" <id> · design include|exclude "<site>"
              design ask "<site>" "<what to change>"   the architect files a redesign in AI suggestions for you to accept
              design open "<site>" [--draft]   open the real site (or its drafted Proposed-home page) in the browser
              design apply "<site>" [--publish] [--yes]   draft the page on the real site; --publish also publishes it and makes it the home page (asks for the tenant name)

            Build the plan (writes to the tenant; dry run first, you confirm, every write audited)
              build [n]                         the build plan for proposal n (default: latest): steps, kind, risk, status
              build login                       consent to the app's write permissions (once per person)
              build preview [--steps a,b | --all-automatic] [--out dir]   the exact requests and PnP scripts, without sending anything
              build run --steps a,b | --all-automatic [--yes]  run automatic steps (asks you to type the tenant)
              build done|skip <step>            record a guided step · build check (mark what is already in place, read-only) · build rebuild (plan again from the proposal, keep finished steps, then check)

            Design and preview
              blueprint show | check | export [--out dir]
              theme generate --primary #0f6cbd [--background #ffffff] [--name "Contoso"] [--out theme.json]
              theme check <theme.json>          WCAG contrast checks for any theme JSON
              preview [--from blueprint|snapshot] [--primary #hex] [--layout standard] [--out mockup.html] [--open]

            Skills (best practice as data)
              skills list | validate | new <id> [--title "..."] | fork <id>
              skills refresh [<id>] [--ai] [--strict]   check every documented rule's quote against its source (--strict: exit 1 if one is gone)

            AI
              ask "<question>" [--role assistant|assessor|architect|brand|governance]
              chat [--role assistant]
              mcp [--role assistant]            serve Hubwright's tools to any MCP client (stdio)

            Other
              proposals [show|accept|reject <id>] · audit [verify] · tenants · doctor
              Every command takes --tenant <name> (default: the connected tenant, or "sample").
            """);
        return 0;
    }

    internal static int Doctor()
    {
        var session = Session();
        Console.WriteLine($"Home      {Hubwright.Runtime.HubwrightHome.Directory}");
        Console.WriteLine($"Tenant    {session.Store.Tenant}{(session.Config.Tenant.ClientId is null ? " (not connected)" : $" · client {session.Config.Tenant.ClientId}")}");
        Console.WriteLine($"Snapshot  {session.Store.Snapshots().FirstOrDefault() ?? "none"}");
        Console.WriteLine($"Skills    {session.Skills.Skills.Count} loaded, {session.Skills.Rules.Count} rules, {session.Skills.Errors.Count} errors");
        foreach (var error in session.Skills.Errors)
        {
            Console.WriteLine($"          ✗ {error.Directory}: {error.Message}");
        }

        Console.WriteLine($"AI        default backend {session.Config.Ai.Backend}");
        foreach (var backend in Enum.GetValues<ModelBackend>())
        {
            var health = session.Models.CheckHealth(backend);
            Console.WriteLine($"          {(health.Ready ? "✓" : "·")} {ModelCatalog.BackendLabel(backend),-38} {health.Detail}");
        }

        return 0;
    }

    internal static string Score(double? score) => score is null ? "–" : Math.Floor(score.Value).ToString(CultureInfo.InvariantCulture);

    internal static void Open(string path)
    {
        if (cli.Flag("open"))
        {
            Process.Start(new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = true })?.Dispose();
        }
    }

    internal static int Tenants()
    {
        foreach (var name in TenantStore.Tenants())
        {
            var store = new TenantStore(name);
            Console.WriteLine($"{name,-20} {store.Snapshots().Count} snapshot(s), {store.Blueprints().Count} blueprint(s)");
        }

        return 0;
    }

    internal static int Audit()
    {
        var log = new AuditLog();
        if (cli.At(1) == "verify")
        {
            var broken = log.Verify();
            Console.WriteLine(broken < 0 ? $"Audit log intact ({log.Read().Count} entries)." : $"Audit log broken at entry {broken + 1}.");
            return broken < 0 ? 0 : 1;
        }

        foreach (var e in log.Read().TakeLast(50))
        {
            Console.WriteLine($"{e.At:yyyy-MM-dd HH:mm}  {e.Actor,-12} {e.Action,-18} {e.Detail}");
        }

        return 0;
    }
}
