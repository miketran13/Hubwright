using Hubwright.Blueprints;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Templates;

namespace Hubwright.Designs;

/// <summary>
/// Proposes a design for every site in a proposal, deterministically: the home site and hubs by their
/// role, other sites by the pattern whose words match their title and purpose, and a general pattern
/// for their kind when nothing matches (marked not confident, for the person to confirm). Changes from
/// an agent or a person are checked by <see cref="Validate"/> before they are kept.
/// </summary>
public static class SiteDesigner
{
    /// <summary>The home page sections a design can use. Each is drawn in previews and becomes a web part or a text block.</summary>
    public static readonly IReadOnlyList<string> Sections =
        ["hero", "news", "events", "quicklinks", "documents", "people", "text", "gallery", "faq", "calendar", "video", "conversations", "tasks"];

    public static readonly IReadOnlyList<string> HeaderLayouts = ["minimal", "compact", "standard", "extended"];

    // Hubwright's own limits for a readable home page, not SharePoint's.
    public const int MaxSections = 10;
    public const int MaxNavigation = 10;
    public const int MaxLabel = 40;
    public const int MaxPurpose = 300;

    public static IReadOnlyList<SiteDesign> Design(IntranetProposal proposal, TenantSnapshot snapshot, IReadOnlyList<SitePattern> patterns, IReadOnlyList<SiteTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(templates);
        var designs = new List<SiteDesign>();
        if (patterns.Count == 0)
        {
            return designs;
        }

        var b = proposal.Blueprint;
        var byTemplate = templates.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var siteByUrl = snapshot.Sites.GroupBy(s => Key(s.Url)).ToDictionary(g => g.Key, g => g.First());
        var planByUrl = proposal.Sites.GroupBy(s => Key(s.Url)).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var regions = proposal.Answers.Intranet.Regions.Where(r => r.Length > 1).ToList();

        void Add(string title, string url, string role, SiteKind kind, string? purpose, string? templateId, bool isNew)
        {
            if (!seen.Add(Key(url)))
            {
                return;
            }

            var (pattern, matched) = role switch
            {
                "home" => (Fallback(patterns, "home", kind), new List<string>()),
                "hub" => (Fallback(patterns, "hub", kind), []),
                // A site named after one of the plan's places is a location site, whatever else its title says.
                _ when regions.FirstOrDefault(r => title.Contains(r, StringComparison.OrdinalIgnoreCase)) is { } region && Match(patterns, "location", null, kind) is { Matched.Count: > 0 } place => (place.Pattern, [region]),
                _ => Match(patterns, title, purpose, kind),
            };
            var template = templateId ?? pattern.Template;
            var t = template is not null && byTemplate.TryGetValue(template, out var found) && TemplateMatcher.SameFamily(found.Kind, kind) ? found : null;
            var design = new SiteDesign
            {
                Url = url.TrimEnd('/'),
                Title = title,
                IsNew = isNew,
                Role = role,
                Kind = kind,
                Pattern = pattern.Id,
                PatternName = pattern.Name,
                Purpose = string.IsNullOrWhiteSpace(purpose) ? pattern.Purpose : purpose.Trim(),
                TemplateId = t?.Id,
                TemplateName = t?.Name,
                Sections = [.. pattern.Sections.Count > 0 ? pattern.Sections : t?.Sections ?? ["hero", "news", "quicklinks"]],
                Navigation = [.. pattern.Navigation],
                HeaderLayout = HeaderLayouts.Contains(pattern.HeaderLayout) ? pattern.HeaderLayout : b.HeaderLayout,
                Audience = pattern.Audience,
                Practices = [.. pattern.Practices],
                Confident = role != "site" || matched.Count > 0,
                Origin = DesignOrigin.Rules,
            };
            design.Reasons.Add(role switch
            {
                "home" => "The home site is the front door of the intranet: it gets the home pattern.",
                "hub" => $"\"{title}\" is a hub: its home page brings together the news and sites of its function.",
                _ when matched.Count > 0 => $"\"{title}\" matches the {pattern.Name} pattern ({string.Join(", ", matched)}).",
                _ => $"Nothing in \"{title}\" points to a purpose, so it starts from the {pattern.Name} pattern for {(kind == SiteKind.Communication ? "communication" : "team")} sites. Confirm what the site is for.",
            });
            design.Reasons.Add(isNew
                ? $"New site{(t is null ? "" : $" from the {t.Name} template")}: this design is drafted as its home page after the site is created."
                : "Existing site: its pages and content stay. The design is drafted as a new page beside them, for the owner to review and publish.");

            if (!isNew && siteByUrl.TryGetValue(Key(url), out var site))
            {
                var actions = planByUrl.TryGetValue(Key(url), out var plan) ? plan.Actions : [];
                design.Blocked = site.Readable == false ? "The scan could not read this site; someone with access checks it first."
                    : site.Kind == SiteKind.Classic || actions.Contains(SiteAction.Modernize) ? "Classic site: modernize it first; modern pages need a modern site."
                    : actions.Contains(SiteAction.Retire) ? "The proposal retires this site."
                    : actions.Contains(SiteAction.Archive) ? "The proposal archives this site."
                    : null;
                if (site.Content.Pages is > 0)
                {
                    design.Reasons.Add($"It has {site.Content.Pages} page(s) today; none is changed or replaced.");
                }
            }

            design.Apply = design.Blocked is null && role is "home" or "hub";
            designs.Add(design);
        }

        Add(b.Home.Title, b.Home.ExistingUrl ?? b.Url(b.Home.Slug), "home", SiteKind.Communication, b.Home.Purpose, b.Home.Template, b.Home.ExistingUrl is null);
        foreach (var hub in b.Hubs)
        {
            Add(hub.Title, hub.ExistingUrl ?? b.Url(hub.Slug), "hub", SiteKind.Communication, hub.Purpose, null, hub.ExistingUrl is null);
        }

        foreach (var site in b.Hubs.SelectMany(h => h.Sites).Concat(b.Standalone))
        {
            Add(site.Title, site.ExistingUrl ?? b.Url(site.Slug), "site", site.Kind, site.Purpose, site.Template, site.ExistingUrl is null);
        }

        foreach (var plan in proposal.Sites)
        {
            var kind = siteByUrl.TryGetValue(Key(plan.Url), out var s) ? s.Kind : plan.Kind;
            Add(plan.Title, plan.Url, "site", kind is SiteKind.Communication ? SiteKind.Communication : kind, s?.Description, null, isNew: false);
        }

        return designs;
    }

    /// <summary>The pattern whose words fit best among those of the site's kind; the kind's general pattern when none does.</summary>
    public static (SitePattern Pattern, List<string> Matched) Match(IReadOnlyList<SitePattern> patterns, string title, string? purpose, SiteKind kind)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        ArgumentNullException.ThrowIfNull(title);

        // Sites are named "{Function} – {Topic}": the topic says more about the purpose than the function.
        var dash = title.LastIndexOf(" – ", StringComparison.Ordinal);
        var topic = dash < 0 ? null : title[(dash + 3)..];
        var best = patterns
            .Where(p => p.Fallback is not ("home" or "hub") && TemplateMatcher.SameFamily(p.Kind, kind))
            .Select(p => (Pattern: p, Fit: TemplateMatcher.Score(p.SuggestFor, title, purpose), Topic: topic is null ? 0 : TemplateMatcher.Score(p.SuggestFor, topic, null).Score))
            .Where(x => x.Fit.Matched.Count > 0)
            .OrderByDescending(x => x.Fit.Score + (x.Topic / 2)).ThenBy(x => x.Pattern.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return best.Pattern is not null ? (best.Pattern, best.Fit.Matched) : (Fallback(patterns, kind == SiteKind.Communication ? "communication" : "team", kind), []);
    }

    private static SitePattern Fallback(IReadOnlyList<SitePattern> patterns, string role, SiteKind kind)
        => patterns.FirstOrDefault(p => p.Fallback == role)
            ?? patterns.FirstOrDefault(p => TemplateMatcher.SameFamily(p.Kind, kind))
            ?? patterns[0];

    /// <summary>The design with a pattern's sections, navigation, header and practices, chosen by a person.</summary>
    public static SiteDesign WithPattern(SiteDesign design, SitePattern pattern, IReadOnlyList<SiteTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(templates);
        var copy = design.Copy();
        copy.Pattern = pattern.Id;
        copy.PatternName = pattern.Name;
        copy.Sections = [.. pattern.Sections];
        copy.Navigation = [.. pattern.Navigation];
        copy.HeaderLayout = pattern.HeaderLayout;
        copy.Audience = pattern.Audience;
        copy.Practices = [.. pattern.Practices];
        copy.Confident = true;
        copy.Origin = DesignOrigin.Person;
        if (design.IsNew && pattern.Template is { } id && templates.FirstOrDefault(t => t.Id == id) is { } t && TemplateMatcher.SameFamily(t.Kind, design.Kind))
        {
            copy.TemplateId = t.Id;
            copy.TemplateName = t.Name;
        }

        copy.Reasons = [$"You chose the {pattern.Name} pattern.", .. design.Reasons.Skip(1)];
        return copy;
    }

    /// <summary>What is wrong with a design an agent or a person made; empty when it can be kept.</summary>
    public static IReadOnlyList<string> Validate(SiteDesign design, IReadOnlyList<SiteTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(templates);
        var errors = new List<string>();
        if (design.Sections.Count is 0 or > MaxSections)
        {
            errors.Add($"A home page needs 1 to {MaxSections} sections.");
        }

        foreach (var section in design.Sections.Where(s => !Sections.Contains(s)))
        {
            errors.Add($"Unknown section '{section}'. Use: {string.Join(", ", Sections)}.");
        }

        if (design.Navigation.Count > MaxNavigation)
        {
            errors.Add($"At most {MaxNavigation} navigation links.");
        }

        foreach (var label in design.Navigation.Where(l => string.IsNullOrWhiteSpace(l) || l.Length > MaxLabel || l.Contains("://", StringComparison.Ordinal) || l.IndexOfAny(['<', '>', '{', '}']) >= 0))
        {
            errors.Add($"Navigation '{label}' must be a plain label of 1 to {MaxLabel} characters (no links or markup).");
        }

        if (!HeaderLayouts.Contains(design.HeaderLayout))
        {
            errors.Add($"Header layout must be one of {string.Join(", ", HeaderLayouts)}.");
        }

        if (design.TemplateId is { } id)
        {
            var template = templates.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (template is null)
            {
                errors.Add($"No template '{id}'.");
            }
            else if (!TemplateMatcher.SameFamily(template.Kind, design.Kind))
            {
                errors.Add($"{template.Name} is a {(template.Kind == SiteKind.Communication ? "communication" : "team")} site template; this is a {(design.Kind == SiteKind.Communication ? "communication" : "team")} site.");
            }
        }

        if (string.IsNullOrWhiteSpace(design.Purpose) || design.Purpose.Length > MaxPurpose)
        {
            errors.Add($"Say what the site is for in 1 to {MaxPurpose} characters.");
        }

        return errors;
    }

    /// <summary>Replaces the proposal's design for the same site. Throws when the site isn't in the proposal.</summary>
    public static void Replace(IntranetProposal proposal, SiteDesign design)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(design);
        var index = proposal.Designs.FindIndex(d => Key(d.Url) == Key(design.Url));
        if (index < 0)
        {
            throw new InvalidOperationException($"{design.Url} has no design in the proposal.");
        }

        proposal.Designs[index] = design;
    }

    /// <summary>The design for a site, by URL or title.</summary>
    public static SiteDesign? Find(IntranetProposal proposal, string site)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(site);
        return proposal.Designs.FirstOrDefault(d => Key(d.Url) == Key(site))
            ?? proposal.Designs.FirstOrDefault(d => string.Equals(d.Title, site.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What goes in a section, for the explanation and the publish instructions.</summary>
    public static string Describe(string section) => section switch
    {
        "hero" => "Hero: up to five tiles for the top stories, campaigns or tasks.",
        "news" => "News: the latest posts from this site (and, on a hub, its sites).",
        "events" or "calendar" => "Events: what's coming up, from the site's event list.",
        "quicklinks" => "Quick links: the six to eight things people come here to do.",
        "documents" => "Documents: the library people need most, or the most-used files.",
        "people" => "People: who to contact, with their profile cards.",
        "text" => "About: who the site is for and what they find here.",
        "gallery" => "Gallery: images that show the work or the place.",
        "video" => "Video: a message or a how-to.",
        "faq" => "FAQ: the questions people ask most, with short answers.",
        "conversations" => "Conversations: the team's discussion, from Teams or Viva Engage.",
        "tasks" => "Tasks: the team's plan.",
        _ => section,
    };

    public static string Key(string url) => url.TrimEnd('/').ToLowerInvariant();
}
