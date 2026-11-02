using System.Globalization;
using Hubwright.Snapshot;

namespace Hubwright.Templates;

/// <summary>Who ships the template, which decides how it is applied.</summary>
public enum TemplateSource
{
    /// <summary>Built into SharePoint: Settings → Apply a site template.</summary>
    Microsoft,

    /// <summary>A look book PnP package (.pnp) applied with PnP PowerShell's Invoke-PnPTenantTemplate.</summary>
    PnpPackage,
}

/// <summary>
/// One ready-made site: a Microsoft-provided site template or a look book PnP package. Catalogued in
/// skills (<c>templates.yaml</c>) with the Microsoft page it comes from and the exact description
/// quoted, so the catalogue can be checked and updated like any rule.
/// </summary>
public sealed class SiteTemplate
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public TemplateSource Source { get; set; } = TemplateSource.Microsoft;

    /// <summary>The kind of site it creates or applies to.</summary>
    public SiteKind Kind { get; set; } = SiteKind.Communication;

    public string? Category { get; set; }

    /// <summary>Microsoft's description, verbatim.</summary>
    public string Description { get; set; } = "";

    /// <summary>The page the template and its description come from.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>A sentence from <see cref="SourceUrl"/> that <c>skills refresh</c> checks is still there.</summary>
    public string? Quote { get; set; }

    public DateOnly? Reviewed { get; set; }

    /// <summary>Words in a site's title or purpose that make this template a good fit.</summary>
    public List<string> SuggestFor { get; set; } = [];

    /// <summary>Creation template: SITEPAGEPUBLISHING#0 (communication) or STS#3 (team without a group).</summary>
    public string? WebTemplate { get; set; }

    /// <summary>Out-of-the-box site design id, for the three that SPSiteManager/create accepts.</summary>
    public string? SiteDesignId { get; set; }

    /// <summary>Microsoft templates: the name PnP PowerShell applies it by (Invoke-PnPSiteDesign -Template).</summary>
    public string? PnpTemplate { get; set; }

    /// <summary>The .pnp file, for look book packages.</summary>
    public string? Package { get; set; }

    public List<string> Parameters { get; set; } = [];

    /// <summary>Who can apply it: SharePoint Online Admin, App Catalog, Regular user.</summary>
    public List<string> Prerequisites { get; set; } = [];

    /// <summary>
    /// Hubwright's sketch of the home page for previews: hero, news, events, quicklinks, documents,
    /// people, text, gallery, faq, calendar, video, conversations, tasks. An approximation for
    /// stakeholders, not Microsoft's exact layout.
    /// </summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>A line for the proposal on how to apply it.</summary>
    public string HowToApply => Source == TemplateSource.Microsoft
        ? "Create the site, then Settings → Apply a site template → " + Name + "."
        : $"Invoke-PnPTenantTemplate -Path .\\{Path.GetFileName(Package ?? "template.pnp")} -Parameters @{{{string.Join(";", Parameters.Select(p => $"\"{p}\"=\"…\""))}}}";
}

/// <summary>A template that fits a site, and why.</summary>
public sealed record TemplateMatch(SiteTemplate Template, double Score, IReadOnlyList<string> Matched);

/// <summary>Picks templates for a site from its title and purpose. Deterministic: word matches, then kind.</summary>
public static class TemplateMatcher
{
    /// <summary>Templates whose keywords appear in the site's title (worth 3) or purpose (worth 1); phrases count double.</summary>
    public static IReadOnlyList<TemplateMatch> Suggest(IEnumerable<SiteTemplate> catalog, string title, string? purpose = null, SiteKind? kind = null, bool packages = true, int take = 3)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var titleWords = Words(title);
        var purposeWords = Words(purpose ?? "");
        var matches = new List<TemplateMatch>();
        foreach (var template in catalog)
        {
            if (!packages && template.Source == TemplateSource.PnpPackage)
            {
                continue;
            }

            if (kind is { } k && !SameFamily(k, template.Kind))
            {
                continue;
            }

            var (score, hits) = Score(template.SuggestFor, title, titleWords, purpose ?? "", purposeWords);
            if (hits.Count == 0)
            {
                continue;
            }

            // Microsoft's own templates win ties: they need no extra tooling.
            matches.Add(new TemplateMatch(template, score + (template.Source == TemplateSource.Microsoft ? 0.25 : 0), hits));
        }

        return [.. matches.OrderByDescending(m => m.Score).ThenBy(m => m.Template.Name, StringComparer.Ordinal).Take(take)];
    }
    /// <summary>How well keywords fit a title (worth 3 each) and purpose (worth 1); phrases count double. Shared with site patterns.</summary>
    public static (double Score, List<string> Matched) Score(IEnumerable<string> keywords, string title, string? purpose)
    {
        ArgumentNullException.ThrowIfNull(keywords);
        ArgumentNullException.ThrowIfNull(title);
        return Score(keywords, title, Words(title), purpose ?? "", Words(purpose ?? ""));
    }

    private static (double Score, List<string> Matched) Score(IEnumerable<string> keywords, string title, HashSet<string> titleWords, string purpose, HashSet<string> purposeWords)
    {
        var score = 0.0;
        var hits = new List<string>();
        foreach (var keyword in keywords)
        {
            var weight = Hits(titleWords, title, keyword) ? 3.0 : Hits(purposeWords, purpose, keyword) ? 1.0 : 0;
            if (weight > 0)
            {
                score += weight * (keyword.Contains(' ', StringComparison.Ordinal) ? 2 : 1);
                hits.Add(keyword);
            }
        }

        return (score, hits);
    }

    /// <summary>The fallback for a site nothing matches: Standard communication or Standard team.</summary>
    public static SiteTemplate? Default(IEnumerable<SiteTemplate> catalog, SiteKind kind)
        => catalog.FirstOrDefault(t => t.Id == (kind == SiteKind.Communication ? "standard-communication" : "standard-team"));

    public static bool SameFamily(SiteKind a, SiteKind b)
        => (a == SiteKind.Communication) == (b == SiteKind.Communication);

    private static bool Hits(HashSet<string> words, string text, string keyword)
        => keyword.Contains(' ', StringComparison.Ordinal)
            ? text.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            : words.Contains(keyword.ToLowerInvariant());

    private static HashSet<string> Words(string text)
        => [.. text.ToLower(CultureInfo.InvariantCulture).Split([' ', '-', '–', '_', '/', ',', '.', '(', ')', ':', '&'], StringSplitOptions.RemoveEmptyEntries)];
}
