namespace Hubwright.Rules;

public enum Severity { Info, Low, Medium, High, Critical }

/// <summary>
/// Why a rule exists. <see cref="Documented"/>: Microsoft states the limit or recommendation, and the
/// rule quotes it. <see cref="Heuristic"/>: a judgment call with a threshold each tenant can change,
/// labelled as such in every report.
/// </summary>
public enum Basis { Documented, Heuristic }

/// <summary>
/// <see cref="Check"/> rules test the snapshot. <see cref="Advisory"/> rules are recommendations
/// Hubwright cannot verify from the data it reads (for example "use DLP to block Anyone links"): they
/// are listed as things to confirm and never scored.
/// </summary>
public enum RuleKind { Check, Advisory }

/// <summary>Which starting mode a rule speaks to.</summary>
public enum AppliesTo { Both, Existing, Scratch }

/// <summary>One rule from a skill's rules.yaml, with its evidence.</summary>
public sealed class RuleDefinition
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public RuleKind Kind { get; init; } = RuleKind.Check;

    public RuleScope Scope { get; init; } = RuleScope.Tenant;

    /// <summary>The violation: when this is true for a target, the rule raises a finding.</summary>
    public string? When { get; init; }

    /// <summary>Which sites or hubs the rule looks at (for example <c>kind == 'Communication'</c>); all when empty.</summary>
    public string? Filter { get; init; }

    public Severity Severity { get; init; } = Severity.Medium;

    /// <summary>How much the rule counts in its category's score; defaults from the severity.</summary>
    public double? Weight { get; init; }

    public Basis Basis { get; init; } = Basis.Heuristic;

    public AppliesTo AppliesTo { get; init; } = AppliesTo.Both;

    public string? Source { get; init; }

    /// <summary>The sentence in the source that this rule enforces, word for word.</summary>
    public string? Quote { get; init; }

    public DateOnly? Reviewed { get; init; }

    /// <summary>The finding text; <c>{fact}</c> placeholders are filled from the target.</summary>
    public string? Message { get; init; }

    public string? Fix { get; init; }

    public IReadOnlyDictionary<string, object?> Params { get; init; } = new Dictionary<string, object?>();

    public string SkillId { get; set; } = "";

    public string SkillVersion { get; set; } = "";

    public string Category { get; set; } = "";

    public double EffectiveWeight => Weight ?? Severity switch
    {
        Severity.Critical => 5,
        Severity.High => 3,
        Severity.Medium => 2,
        Severity.Low => 1,
        _ => 0,
    };
}

/// <summary>A tenant's changes to a rule: turn it off, change its severity, or change its thresholds.</summary>
public sealed class RuleOverride
{
    public bool Disabled { get; set; }

    public Severity? Severity { get; set; }

    public Dictionary<string, object?> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
