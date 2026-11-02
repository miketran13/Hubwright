using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Hubwright.Blueprints;
using Hubwright.Rules;
using Hubwright.Snapshot;
using Hubwright.Storage;
using Hubwright.Theming;

namespace Hubwright.Agents;

/// <summary>
/// The tools every role gets. They read the workspace and return text. The tools that write,
/// propose_blueprint and propose_site_design, file a proposal for a person to accept and change nothing themselves.
/// </summary>
public sealed class ToolCatalog(Workspace workspace, ProposalStore proposals, string role)
{
    public IReadOnlyList<AIFunction> All() =>
    [
        AIFunctionFactory.Create(SnapshotSummary, "get_snapshot_summary", "Tenant overview: counts, scores per category, the worst findings and what could not be read."),
        AIFunctionFactory.Create(ListFindings, "list_findings", "Findings, optionally filtered by category or minimum severity (info, low, medium, high, critical)."),
        AIFunctionFactory.Create(RuleDetail, "get_rule", "One rule: what it checks, its basis, source and quote, its parameters, and every target that failed."),
        AIFunctionFactory.Create(QuerySites, "query_sites", "Sites matching a Hubwright rule expression over site facts, for example \"ownerCount == 0 and isPublic\". Use list_facts('site') for names."),
        AIFunctionFactory.Create(ListFacts, "list_facts", "The facts rule expressions can use, for scope tenant, site or hub."),
        AIFunctionFactory.Create(ListSkills, "list_skills", "The skills in effect, with versions and sources."),
        AIFunctionFactory.Create(GetSkill, "get_skill", "A skill's guidance (best practice, rationale, how to fix) and its rules."),
        AIFunctionFactory.Create(GetBlueprint, "get_blueprint", "The tenant's latest blueprint as JSON, or a note that there is none."),
        AIFunctionFactory.Create(CheckBlueprint, "check_blueprint", "Runs every rule over the latest blueprint, as if it were built, and returns the findings."),
        AIFunctionFactory.Create(PreviewTheme, "preview_theme", "Generates a SharePoint theme from a brand colour (#rrggbb) and returns the palette and WCAG contrast checks."),
        AIFunctionFactory.Create(ProposeBlueprint, "propose_blueprint", "Files a complete blueprint JSON as a proposal for the admin to review. Returns the proposal id."),
        AIFunctionFactory.Create(GetIntranetProposal, "get_intranet_proposal", "The tenant's latest intranet proposal: scores before and after, target hubs, the plan for every existing site, new sites with templates, roadmap and decisions (Markdown)."),
        AIFunctionFactory.Create(ListSiteTemplates, "list_site_templates", "Microsoft's built-in SharePoint site templates and the look book PnP packages, with descriptions and how to apply them."),
        AIFunctionFactory.Create(SuggestSiteTemplate, "suggest_site_template", "The best-fitting ready-made templates for a site, from its title and purpose."),
        AIFunctionFactory.Create(ListSitePatterns, "list_site_patterns", "The site design patterns (purpose → template, home page sections, navigation, practices) from the site-design skill."),
        AIFunctionFactory.Create(GetSiteDesign, "get_site_design", "The proposed design of one site in the latest intranet proposal (by URL or title), or a one-line list of every site's design when empty."),
        AIFunctionFactory.Create(ProposeSiteDesign, "propose_site_design", "Files a redesign of one site for the admin to accept: start from a pattern and/or give the sections, navigation labels, purpose and header. Checked first; returns the errors to fix, or the suggestion id. Changes nothing in the tenant."),
    ];

    private string ListSitePatterns()
        => string.Join("\n", workspace.Skills.Patterns.Select(p =>
            $"- {p.Id} · {p.Name} ({(p.Kind == SiteKind.Communication ? "communication" : "team")}{(p.Fallback is null ? "" : ", fallback for " + p.Fallback)}): {p.Purpose} Sections: {string.Join(", ", p.Sections)}. Navigation: {string.Join(" · ", p.Navigation)}. Template: {p.Template ?? "none"}."))
            + $"\nSections you can use: {string.Join(", ", Designs.SiteDesigner.Sections)}. Header layouts: {string.Join(", ", Designs.SiteDesigner.HeaderLayouts)}.";

    private string GetSiteDesign([Description("The site's URL or title; empty for every site.")] string? site = null)
    {
        if (SiteDesignProposals.Latest(workspace.Store) is not { } latest)
        {
            return "No intranet proposal yet. The admin starts one from the Intranet plan page or `hubwright proposal new`.";
        }

        if (string.IsNullOrWhiteSpace(site))
        {
            return latest.Proposal.Designs.Count == 0
                ? "The proposal has no site designs (it was made before the site-design skill). Make a new proposal."
                : string.Join("\n", latest.Proposal.Designs.Select(d => $"- {d.Title} ({d.Url}): {d.PatternName}{(d.Confident ? "" : " (not confident)")}{(d.Apply ? ", in build" : "")}{(d.Blocked is null ? "" : ", blocked")}"));
        }

        return Designs.SiteDesigner.Find(latest.Proposal, site) is { } design ? SiteDesignProposals.Describe(design) : $"No site {site} in the proposal. Call get_site_design with no site for the list.";
    }

    private string ProposeSiteDesign(
        [Description("The site's URL or title.")] string site,
        [Description("One paragraph for the admin: what changes and why.")] string summary,
        [Description("A pattern id to start from (list_site_patterns); empty keeps the current one.")] string? pattern = null,
        [Description("Home page sections top to bottom, comma-separated; empty keeps the pattern's.")] string? sections = null,
        [Description("Navigation link labels, comma-separated; empty keeps the pattern's.")] string? navigation = null,
        [Description("What the site is for, one or two sentences; empty keeps it.")] string? purpose = null,
        [Description("Header layout: minimal, compact, standard or extended; empty keeps it.")] string? headerLayout = null)
    {
        if (SiteDesignProposals.Latest(workspace.Store) is not { } latest)
        {
            return "No intranet proposal yet.";
        }

        if (Designs.SiteDesigner.Find(latest.Proposal, site) is not { } current)
        {
            return $"No site {site} in the proposal.";
        }

        var design = current.Copy();
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            var p = workspace.Skills.Patterns.FirstOrDefault(x => string.Equals(x.Id, pattern.Trim(), StringComparison.OrdinalIgnoreCase));
            if (p is null)
            {
                return $"No pattern {pattern}. Call list_site_patterns.";
            }

            design = Designs.SiteDesigner.WithPattern(current, p, workspace.Skills.Templates);
        }

        static List<string> Split(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (!string.IsNullOrWhiteSpace(sections))
        {
            design.Sections = [.. Split(sections).Select(x => x.ToLowerInvariant())];
            design.Pattern = "custom";
            design.PatternName = $"{design.PatternName} (changed)";
        }

        if (!string.IsNullOrWhiteSpace(navigation))
        {
            design.Navigation = Split(navigation);
        }

        if (!string.IsNullOrWhiteSpace(purpose))
        {
            design.Purpose = purpose.Trim();
        }

        if (!string.IsNullOrWhiteSpace(headerLayout))
        {
            design.HeaderLayout = headerLayout.Trim().ToLowerInvariant();
        }

        design.Reasons = [$"Proposed by the {role} agent: {summary}", .. current.Reasons.Skip(1)];
        var (filed, errors) = SiteDesignProposals.File(workspace, proposals, latest.Folder, current, design, summary, role);
        return filed is null
            ? "Not filed. Fix these and call propose_site_design again:\n- " + string.Join("\n- ", errors)
            : $"Filed suggestion {filed.Id}. The admin previews it and accepts or rejects it in AI suggestions (or `hubwright proposals show {filed.Id}`).";
    }

    private string GetIntranetProposal()
    {
        var latest = workspace.Store.IntranetProposals().FirstOrDefault();
        return latest is null
            ? "No intranet proposal yet. The admin starts one from the Proposal page or `hubwright proposal new`."
            : Proposals.ProposalWriter.Markdown(TenantStore.LoadIntranetProposal(latest));
    }

    private string ListSiteTemplates([Description("microsoft, package, or empty for both.")] string? source = null)
    {
        var lines = workspace.Skills.Templates
            .Where(t => string.IsNullOrWhiteSpace(source) || (source.StartsWith("pack", StringComparison.OrdinalIgnoreCase) ? t.Source == Templates.TemplateSource.PnpPackage : t.Source == Templates.TemplateSource.Microsoft))
            .Select(t => $"- {t.Id} · {t.Name} ({(t.Source == Templates.TemplateSource.Microsoft ? "built in" : "look book package")}, {(t.Kind == SiteKind.Communication ? "communication" : "team")} site): {t.Description}");
        return string.Join("\n", lines);
    }

    private string SuggestSiteTemplate(
        [Description("The site's title, for example \"HR – Benefits\".")] string title,
        [Description("What the site is for.")] string? purpose = null,
        [Description("communication or team; empty for either.")] string? kind = null)
    {
        SiteKind? k = kind is null ? null : kind.StartsWith("team", StringComparison.OrdinalIgnoreCase) ? SiteKind.TeamGroup : SiteKind.Communication;
        var matches = Templates.TemplateMatcher.Suggest(workspace.Skills.Templates, title, purpose, k);
        return matches.Count == 0
            ? "Nothing specific fits: use Standard communication or Standard team."
            : string.Join("\n", matches.Select(m => $"- {m.Template.Name} ({m.Template.Id}), matched {string.Join(", ", m.Matched)}: {m.Template.HowToApply}"));
    }

    [Description("Tenant overview.")]
    private string SnapshotSummary()
    {
        var a = workspace.RequireAssessment();
        var s = a.Snapshot;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tenant {s.Tenant}, snapshot {s.CapturedAt:yyyy-MM-dd} ({s.Source}{(s.Scope is null ? "" : ", scope " + s.Scope)}).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{s.Sites.Count} sites ({s.Sites.Count(x => x.Kind == SiteKind.Communication)} communication, {s.Sites.Count(x => x.Kind is SiteKind.TeamGroup or SiteKind.TeamNoGroup)} team, {s.Sites.Count(x => x.Kind == SiteKind.Classic)} classic), {s.Hubs.Count} hubs, home site: {s.HomeSite?.Title ?? "none or unknown"}.");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Overall score {Format(a.Overall)} ({Scoring.Assessment.Grade(a.Overall)}).");
        foreach (var c in a.Categories)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {c.Category}: {Format(c.Score)} ({c.Failed} of {c.Rules} rules failed, {c.NotAssessed} not assessed)");
        }

        sb.AppendLine("Top findings:");
        foreach (var group in a.Findings.GroupBy(f => f.RuleId).Take(10))
        {
            var f = group.First();
            sb.AppendLine(CultureInfo.InvariantCulture, $"- [{f.Severity}] {f.RuleId}: {f.Title} ({group.Count()} target(s), {Reports.ReportWriter.BasisLabel(f.Basis)})");
        }

        if (s.ScanIssues.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Could not read: {string.Join("; ", s.ScanIssues.GroupBy(i => i.Area).Select(g => $"{g.Key} ({g.Count()})"))}.");
        }

        return sb.ToString();
    }

    private string ListFindings(
        [Description("Category such as hubs, sharing, permissions; empty for all.")] string? category = null,
        [Description("Minimum severity: info, low, medium, high or critical.")] string? minSeverity = null,
        [Description("Most findings to return.")] int limit = 40)
    {
        var a = workspace.RequireAssessment();
        var min = Enum.TryParse<Severity>(minSeverity, true, out var s) ? s : Severity.Info;
        var items = a.Findings
            .Where(f => f.Severity >= min && (string.IsNullOrWhiteSpace(category) || string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Clamp(limit, 1, 200))
            .Select(f => $"[{f.Severity}] {f.RuleId} · {f.Message}{(f.Target == "tenant" ? "" : $" · {f.Target}")}");
        var text = string.Join("\n", items);
        return text.Length == 0 ? "No findings match." : text;
    }

    private string RuleDetail([Description("Rule id, for example hubs.nav-sites.")] string ruleId)
    {
        var a = workspace.RequireAssessment();
        var result = a.Results.FirstOrDefault(r => string.Equals(r.Rule.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        if (result is null)
        {
            return $"No rule {ruleId}.";
        }

        var r = result.Rule;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{r.Id}: {r.Title} ({r.Severity}, {Reports.ReportWriter.BasisLabel(r.Basis)}, skill {r.SkillId} {r.SkillVersion})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Status {result.Status}: {result.Failed} failed of {result.Assessed} assessed, {result.Unknown} unknown.");
        if (r.When is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Condition ({r.Scope}): {r.When}{(r.Filter is null ? "" : $" where {r.Filter}")}");
        }

        if (r.Params.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Parameters: {string.Join(", ", r.Params.Select(p => $"{p.Key}={p.Value}"))}");
        }

        if (r.Source is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {r.Source}\nQuote: \"{r.Quote}\" (reviewed {r.Reviewed:yyyy-MM-dd})");
        }

        if (r.Fix is not null)
        {
            sb.AppendLine("Fix: " + r.Fix);
        }

        foreach (var f in a.Findings.Where(f => f.RuleId == r.Id).Take(50))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {f.Message} ({f.Target})");
        }

        return sb.ToString();
    }

    private string QuerySites(
        [Description("A rule expression over site facts, for example \"daysSinceActivity > 365\".")] string where,
        [Description("Most sites to return.")] int limit = 50)
    {
        var snapshot = workspace.RequireAssessment().Snapshot;
        try
        {
            var expression = Expression.Parse(where);
            var empty = new Dictionary<string, object?>();
            var matches = snapshot.Sites
                .Where(site => expression.Test(new SnapshotFacts(RuleScope.Site, new FactContext(snapshot, Site: site), empty)) == true)
                .ToList();
            return matches.Count == 0
                ? "No sites match."
                : $"{matches.Count} site(s) match.\n" + string.Join("\n", matches.Take(Math.Clamp(limit, 1, 500)).Select(s => $"- {s.Title} · {s.Url} · {s.Kind}{(s.IsHub ? " · hub" : "")}"));
        }
        catch (RuleException ex)
        {
            return "Expression error: " + ex.Message;
        }
    }

    private static string ListFacts([Description("tenant, site or hub")] string scope)
        => Enum.TryParse<RuleScope>(scope, true, out var s)
            ? string.Join("\n", FactCatalog.For(s).Select(f => $"{f.Name}: {f.Description}"))
            : "Scope must be tenant, site or hub.";

    private string ListSkills()
        => string.Join("\n", workspace.Skills.Skills.Select(s => $"- {s.Id} {s.Version} ({s.Layer}): {s.Title}, {s.Rules.Count} rules · {string.Join(", ", s.Sources.Select(x => x.Url))}"));

    private string GetSkill([Description("Skill id, for example hub-sites.")] string id)
    {
        var skill = workspace.Skills.Find(id);
        return skill is null
            ? $"No skill {id}. Known: {string.Join(", ", workspace.Skills.Skills.Select(s => s.Id))}."
            : $"{skill.Title} {skill.Version}\n\n{skill.Guidance}\n\nRules:\n{string.Join("\n", skill.Rules.Select(r => $"- {r.Id} ({r.Basis}, {r.Severity}): {r.Title}"))}";
    }

    private string GetBlueprint() => workspace.Blueprint?.ToJson() ?? "No blueprint yet. Run `hubwright blueprint new`.";

    private string CheckBlueprint()
    {
        var blueprint = workspace.Blueprint;
        if (blueprint is null)
        {
            return "No blueprint yet.";
        }

        var a = workspace.Assess(blueprint.ToSnapshot());
        return $"Blueprint score {Format(a.Overall)}. Findings:\n" + (a.Findings.Count == 0 ? "none" : string.Join("\n", a.Findings.Select(f => $"- [{f.Severity}] {f.RuleId}: {f.Message}")));
    }

    private static string PreviewTheme([Description("Brand colour as #rrggbb.")] string primary, [Description("Page background, default #ffffff.")] string? background = null)
    {
        if (!Rgb.TryParse(primary, out var color))
        {
            return "Not a colour; use #rrggbb.";
        }

        var page = Rgb.TryParse(background, out var bg) ? bg : Rgb.White;
        var theme = ThemeGenerator.Generate(new ThemeInput("Preview", color) { Background = page, Text = page.IsDark ? new Rgb(0xf3, 0xf2, 0xf1) : new Rgb(0x32, 0x31, 0x30) });
        var suggestion = ThemeGenerator.SuggestAccessiblePrimary(color, page);
        return theme.ToJson() + "\nChecks:\n" + string.Join("\n", theme.Checks.Select(c => $"- {c.Usage}: {c.Ratio:0.00}:1 (needs {c.Required}) {(c.Passes ? "pass" : "FAIL")}"))
            + (theme.Accessible ? "" : $"\nNearest accessible brand colour: {suggestion?.Hex ?? "none found"}");
    }

    private string ProposeBlueprint([Description("The complete blueprint JSON.")] string blueprintJson, [Description("One paragraph: what changed and why.")] string summary)
    {
        Blueprint blueprint;
        try
        {
            blueprint = Blueprint.FromJson(blueprintJson);
        }
        catch (JsonException ex)
        {
            return "That is not valid blueprint JSON: " + ex.Message;
        }

        var check = workspace.Assess(blueprint.ToSnapshot());
        var proposal = proposals.Add(new Proposal
        {
            Kind = ProposalKind.Blueprint,
            Title = $"Blueprint: {blueprint.Name}",
            Summary = summary,
            Target = workspace.Store.Tenant,
            Files = new() { ["blueprint.json"] = blueprint.ToJson() },
            ProposedBy = role,
        });
        return $"Filed proposal {proposal.Id} (blueprint score {Format(check.Overall)}, {check.Findings.Count} findings). The admin reviews it with `hubwright proposals show {proposal.Id}`.";
    }

    private static string Format(double? score) => score is null ? "unknown" : Math.Floor(score.Value).ToString(CultureInfo.InvariantCulture);
}
