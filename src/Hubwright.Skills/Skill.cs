using Hubwright.Rules;

namespace Hubwright.Skills;

/// <summary>Where a skill was loaded from. Later layers replace earlier ones with the same id.</summary>
public enum SkillLayer { BuiltIn, Organization, User }

/// <summary>A page a skill is grounded in, and when someone last checked it.</summary>
public sealed class SkillSource
{
    public string Url { get; set; } = "";

    public string? Title { get; set; }

    public DateOnly? Reviewed { get; set; }

    /// <summary>Set when the page is archived or retired, so reviewers know to find its successor.</summary>
    public string? Note { get; set; }
}

/// <summary>A question a skill adds to the from-scratch checklist, for decisions no tenant data can answer.</summary>
public sealed class SkillQuestion
{
    public string Id { get; set; } = "";

    public string Text { get; set; } = "";

    public string? Why { get; set; }

    /// <summary>Answers to pick from; empty means free text.</summary>
    public List<string> Choices { get; set; } = [];

    public string? Default { get; set; }
}

/// <summary>
/// One skill: a folder with SKILL.md (front matter + guidance the AI reads) and rules.yaml (checks).
/// Skills are data, so anyone can add or improve one with a pull request or a folder of their own.
/// </summary>
public sealed class Skill
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Version { get; init; }

    public required string Category { get; init; }

    public string? Description { get; init; }

    public AppliesTo AppliesTo { get; init; } = AppliesTo.Both;

    public string? Owner { get; init; }

    public IReadOnlyList<SkillSource> Sources { get; init; } = [];

    /// <summary>The markdown after the front matter: best practice, rationale, how to fix.</summary>
    public string Guidance { get; init; } = "";

    public IReadOnlyList<RuleDefinition> Rules { get; init; } = [];

    public IReadOnlyList<SkillQuestion> Questions { get; init; } = [];

    /// <summary>Ready-made sites this skill catalogues (templates.yaml): Microsoft site templates and look book packages.</summary>
    public IReadOnlyList<Templates.SiteTemplate> Templates { get; init; } = [];

    /// <summary>Site purposes and their designs (patterns.yaml): template, home page sections, navigation, practices.</summary>
    public IReadOnlyList<Designs.SitePattern> Patterns { get; init; } = [];

    /// <summary>Security baseline settings (baseline.yaml): target values per profile, each cited.</summary>
    public IReadOnlyList<Security.BaselineSetting> Baseline { get; init; } = [];

    /// <summary>What, why, how and an example for suggestions and steps (hints.yaml).</summary>
    public IReadOnlyList<Hints.Hint> Hints { get; init; } = [];

    public required string Directory { get; init; }

    public SkillLayer Layer { get; init; }

    public Version ParsedVersion => System.Version.TryParse(Version, out var v) ? v : new Version(0, 0);
}
