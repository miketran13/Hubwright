using Hubwright.Rules;
using Hubwright.Snapshot;

namespace Hubwright.Designs;

/// <summary>
/// A site purpose and the design that serves it: the template to start from, the home page sections,
/// the site's own navigation, the header and who reads and edits it, with the practices behind each
/// choice. Catalogued in skills (<c>patterns.yaml</c>). Patterns are Hubwright's judgment
/// (<see cref="Basis.Heuristic"/>, with a note) unless a quote from Microsoft backs them.
/// </summary>
public sealed class SitePattern
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>What a site with this purpose is for, in one line.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>Communication (most people read, few edit) or team (members work together).</summary>
    public SiteKind Kind { get; set; } = SiteKind.Communication;

    /// <summary>Words in a site's title or purpose that make this pattern a fit.</summary>
    public List<string> SuggestFor { get; set; } = [];

    /// <summary>Which sites fall back to this pattern when no words match: home, hub, communication or team.</summary>
    public string? Fallback { get; set; }

    /// <summary>The ready-made template (templates.yaml id) a new site with this purpose starts from.</summary>
    public string? Template { get; set; }

    /// <summary>The home page, top to bottom, in the section vocabulary of <see cref="SiteDesigner.Sections"/>.</summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>The site's own navigation: link labels, in order. Pages are created by the owner.</summary>
    public List<string> Navigation { get; set; } = [];

    /// <summary>minimal, compact, standard or extended.</summary>
    public string HeaderLayout { get; set; } = "standard";

    /// <summary>Who reads and who edits.</summary>
    public string Audience { get; set; } = "";

    /// <summary>The practices the design follows, one sentence each: the explanation shown with it.</summary>
    public List<string> Practices { get; set; } = [];

    public Basis Basis { get; set; } = Basis.Heuristic;

    /// <summary>What a heuristic pattern is based on.</summary>
    public string? Note { get; set; }

    public string? Source { get; set; }

    public string? Quote { get; set; }

    public DateOnly? Reviewed { get; set; }

    public string SkillId { get; set; } = "";
}
