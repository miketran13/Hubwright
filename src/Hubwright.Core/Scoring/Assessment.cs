using Hubwright.Rules;
using Hubwright.Snapshot;

namespace Hubwright.Scoring;

/// <summary>A category's score: the weighted share of its assessed rules that passed.</summary>
public sealed record CategoryScore(string Category, double? Score, int Rules, int Failed, int NotAssessed)
{
    public string Grade => Assessment.Grade(Score);
}

/// <summary>
/// The result of running every skill's rules over a snapshot: findings, per-rule outcomes,
/// category scores and the skill versions used, so any report can be reproduced.
/// </summary>
public sealed class Assessment
{
    public required TenantSnapshot Snapshot { get; init; }

    public required IReadOnlyList<RuleResult> Results { get; init; }

    public required IReadOnlyList<Finding> Findings { get; init; }

    public required IReadOnlyList<CategoryScore> Categories { get; init; }

    /// <summary>0–100; null when nothing could be assessed.</summary>
    public double? Overall { get; init; }

    /// <summary>skill id → version, as loaded.</summary>
    public required IReadOnlyDictionary<string, string> SkillVersions { get; init; }

    public DateTimeOffset RunAt { get; init; } = DateTimeOffset.UtcNow;

    public IEnumerable<RuleResult> Advisories => Results.Where(r => r.Status == RuleStatus.Advisory);

    public IEnumerable<RuleResult> Errors => Results.Where(r => r.Status == RuleStatus.Error);

    public static Assessment Run(
        TenantSnapshot snapshot,
        IReadOnlyList<RuleDefinition> rules,
        IReadOnlyDictionary<string, string> skillVersions,
        IReadOnlyDictionary<string, RuleOverride>? overrides = null)
    {
        var applicable = rules.Where(r => r.AppliesTo == AppliesTo.Both
            || (r.AppliesTo == AppliesTo.Scratch) == (snapshot.Source == SnapshotSource.Blueprint)).ToList();
        var results = RuleEngine.Evaluate(snapshot, applicable, overrides, out var findings);
        var categories = Score(results);
        var weight = categories.Where(c => c.Score is not null).Sum(c => CategoryWeight(results, c.Category));
        double? overall = weight == 0
            ? null
            : categories.Where(c => c.Score is not null).Sum(c => c.Score!.Value * CategoryWeight(results, c.Category)) / weight;

        return new Assessment
        {
            Snapshot = snapshot,
            Results = results,
            Findings = [.. findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Category, StringComparer.Ordinal).ThenBy(f => f.RuleId, StringComparer.Ordinal)],
            Categories = categories,
            Overall = overall,
            SkillVersions = skillVersions,
        };
    }

    private static List<CategoryScore> Score(IReadOnlyList<RuleResult> results)
        => [.. results
            .Where(r => r.Status != RuleStatus.Advisory)
            .GroupBy(r => r.Rule.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var scored = g.Where(r => r.PassRatio is not null && r.Rule.EffectiveWeight > 0).ToList();
                var weight = scored.Sum(r => r.Rule.EffectiveWeight);
                double? score = weight == 0 ? null : 100 * scored.Sum(r => r.PassRatio!.Value * r.Rule.EffectiveWeight) / weight;
                return new CategoryScore(g.Key, score, g.Count(), g.Count(r => r.Status == RuleStatus.Fail), g.Count(r => r.Status is RuleStatus.NotAssessed));
            })
            .OrderBy(c => c.Category, StringComparer.Ordinal)];

    private static double CategoryWeight(IReadOnlyList<RuleResult> results, string category)
        => results.Where(r => string.Equals(r.Rule.Category, category, StringComparison.OrdinalIgnoreCase) && r.PassRatio is not null).Sum(r => r.Rule.EffectiveWeight);

    public static string Grade(double? score) => score switch
    {
        null => "–",
        >= 90 => "A",
        >= 80 => "B",
        >= 65 => "C",
        >= 50 => "D",
        _ => "E",
    };
}
