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

/// <summary>Stage 2, Assess: reports, findings, snapshot diffs and the facts rules can use.</summary>
internal static partial class Commands
{
    internal static async Task<int> AssessAsync()
    {
        var session = Session();
        var assessment = session.Workspace.RequireAssessment();
        var format = (cli.Option("format") ?? "md").ToLowerInvariant() switch
        {
            "html" => ReportFormat.Html,
            "json" => ReportFormat.Json,
            _ => ReportFormat.Markdown,
        };

        string? narrative = null;
        if (cli.Flag("ai"))
        {
            Console.Error.WriteLine($"  asking the assessor ({session.Config.Ai.BackendFor(Roles.Assessor)})…");
            narrative = await session.Runner.RunAsync(Roles.Assessor, "Write the assessment summary and the 30/60/90-day plan for this tenant.");
        }

        var report = ReportWriter.Write(assessment, format, narrative);
        var extension = format switch { ReportFormat.Html => "html", ReportFormat.Json => "json", _ => "md" };
        var outPath = cli.Option("out") ?? (format == ReportFormat.Markdown && !cli.Flag("open") ? null : session.Store.SaveReport($"assessment-{DateTime.UtcNow:yyyyMMdd-HHmm}.{extension}", report));
        if (outPath is null)
        {
            Console.WriteLine(report);
        }
        else
        {
            if (cli.Option("out") is not null)
            {
                File.WriteAllText(outPath, report);
            }

            Console.WriteLine($"Overall {Score(assessment.Overall)} ({Hubwright.Scoring.Assessment.Grade(assessment.Overall)}) · {assessment.Findings.Count} findings · {outPath}");
            Open(outPath);
        }

        foreach (var error in assessment.Errors)
        {
            Console.Error.WriteLine($"  ⚠ {error.Error}");
        }

        UnreadNote(assessment.Snapshot);
        return 0;
    }

    /// <summary>One line on stderr when the scan could not open some sites, so a score is never read as covering them.</summary>
    internal static void UnreadNote(Hubwright.Snapshot.TenantSnapshot snapshot)
    {
        if (Hubwright.Snapshot.SiteAccess.Unread(snapshot) is { Count: > 0 } unread)
        {
            Console.Error.WriteLine($"  ⚠ {Hubwright.Snapshot.SiteAccess.Summary(unread)}: `hubwright access` lists them and how to get access.");
        }
    }

    /// <summary>The Score, Findings, Structure and Reports pages' warning: the sites the scan could not open.</summary>
    internal static int Access()
    {
        var snapshot = Session().Workspace.Snapshot ?? throw new InvalidOperationException("No snapshot yet: scan, import or load a sample first.");
        var unread = Hubwright.Snapshot.SiteAccess.Unread(snapshot);
        if (Csv(Hubwright.Snapshot.SiteAccess.Csv(unread)))
        {
            return 0;
        }

        if (unread.Count == 0)
        {
            Console.WriteLine("The scan read every site it found.");
            return 0;
        }

        Console.WriteLine($"⚠ {Hubwright.Snapshot.SiteAccess.Summary(unread)}.");
        Console.WriteLine(Hubwright.Snapshot.SiteAccess.Explanation);
        Console.WriteLine();
        foreach (var s in unread)
        {
            Console.WriteLine($"  {s.Title,-34} {(s.Level == Hubwright.Snapshot.SiteAccessLevel.CannotOpen ? "can't open" : "permissions hidden"),-19} {(s.LastActivity is { } last ? $"active {last:yyyy-MM-dd}" : ""),-18} {s.Url}");
        }

        Console.WriteLine();
        Console.WriteLine($"To see or get access (Active sites: {Hubwright.Snapshot.SiteAccess.ActiveSitesLink}):");
        var n = 0;
        foreach (var step in Hubwright.Snapshot.SiteAccess.HowToGetAccess)
        {
            Console.WriteLine($"  {++n}. {step}");
        }

        Console.WriteLine($"  Manage site admins: {Hubwright.Snapshot.SiteAccess.ManageSiteAdminsDoc}");
        return 0;
    }

    internal static int Findings()
    {
        var assessment = Session().Workspace.RequireAssessment();
        if (Csv(Monitoring.CsvExport.Findings(assessment)))
        {
            return 0;
        }

        var min = Enum.TryParse<Severity>(cli.Option("severity"), true, out var s) ? s : Severity.Info;
        var category = cli.Option("category");
        foreach (var f in assessment.Findings.Where(f => f.Severity >= min && (category is null || string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase))))
        {
            Console.WriteLine($"{f.Severity,-8} {f.RuleId,-34} {f.Message}");
        }

        UnreadNote(assessment.Snapshot);
        return 0;
    }

    internal static int Diff()
    {
        var session = Session();
        TenantSnapshot Load(string path) => TenantSnapshot.FromJson(File.ReadAllText(path));
        var (older, newer) = cli.At(1) is { } a && cli.At(2) is { } b
            ? (Load(a), Load(b))
            : session.Store.Snapshots() is { Count: >= 2 } list ? (Load(list[1]), Load(list[0])) : throw new InvalidOperationException("Need two snapshots: pass two files or scan twice.");
        Console.WriteLine(AssessmentDiff.Between(session.Workspace.Assess(older), session.Workspace.Assess(newer)).ToMarkdown());
        return 0;
    }

    internal static int Facts()
    {
        foreach (var scope in cli.At(1) is { } s ? [Enum.Parse<RuleScope>(s, true)] : Enum.GetValues<RuleScope>())
        {
            Console.WriteLine($"# {scope.ToString().ToLowerInvariant()} facts");
            foreach (var fact in FactCatalog.For(scope))
            {
                Console.WriteLine($"  {fact.Name,-36} {fact.Description}");
            }

            Console.WriteLine();
        }

        return 0;
    }
}
