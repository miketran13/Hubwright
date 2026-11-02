using Hubwright.Snapshot;

namespace Hubwright.Designs;

/// <summary>Who chose a design: Hubwright's rules, an agent's proposal that a person accepted, or the person.</summary>
public enum DesignOrigin { Rules, Agent, Person }

/// <summary>
/// The proposed design of one site, existing or new: its purpose, the pattern it follows, the home page
/// sections, its navigation and header, and why. Shown as a preview with sample content; applied in a
/// Build run as a draft page that the site's owner reviews and publishes, or publishes on request.
/// </summary>
public sealed class SiteDesign
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>A site the proposal creates (the template applies), or one that exists (a draft page, content kept).</summary>
    public bool IsNew { get; set; }

    /// <summary>home, hub or site.</summary>
    public string Role { get; set; } = "site";

    public SiteKind Kind { get; set; }

    /// <summary>The pattern id, or "custom" once someone changes the sections.</summary>
    public string Pattern { get; set; } = "";

    public string PatternName { get; set; } = "";

    public string Purpose { get; set; } = "";

    public string? TemplateId { get; set; }

    public string? TemplateName { get; set; }

    public List<string> Sections { get; set; } = [];

    public List<string> Navigation { get; set; } = [];

    public string HeaderLayout { get; set; } = "standard";

    public string Audience { get; set; } = "";

    /// <summary>Why this design: which words matched, what the site is today.</summary>
    public List<string> Reasons { get; set; } = [];

    /// <summary>The practices it follows (from the pattern).</summary>
    public List<string> Practices { get; set; } = [];

    /// <summary>False when nothing about the site pointed to a purpose: the person should confirm it.</summary>
    public bool Confident { get; set; }

    public DesignOrigin Origin { get; set; }

    /// <summary>The agent's or person's one-line reason for a change.</summary>
    public string? ChangeNote { get; set; }

    /// <summary>Whether the Build plan drafts this design's home page. Home and hubs start included.</summary>
    public bool Apply { get; set; }

    /// <summary>
    /// Whether the Build also publishes the drafted page and makes it the site's home page (an
    /// automatic step that records the previous home page). Off: the owner publishes it by hand.
    /// Only ever set by the person, after a confirmation that says what changes.
    /// </summary>
    public bool Publish { get; set; }

    /// <summary>Why the design can't be applied yet (classic, not readable, being retired); null when it can.</summary>
    public string? Blocked { get; set; }

    public SiteDesign Copy() => new()
    {
        Url = Url, Title = Title, IsNew = IsNew, Role = Role, Kind = Kind, Pattern = Pattern, PatternName = PatternName, Purpose = Purpose,
        TemplateId = TemplateId, TemplateName = TemplateName, Sections = [.. Sections], Navigation = [.. Navigation], HeaderLayout = HeaderLayout,
        Audience = Audience, Reasons = [.. Reasons], Practices = [.. Practices], Confident = Confident, Origin = Origin, ChangeNote = ChangeNote,
        Apply = Apply, Publish = Publish, Blocked = Blocked,
    };
}
