using System.Globalization;
using System.Text.RegularExpressions;
using Hubwright.Snapshot;

namespace Hubwright.Rules;

public enum RuleStatus { Pass, Fail, NotAssessed, NotApplicable, Advisory, Error }

/// <summary>A rule that failed for one target, with the evidence a reader needs to trust it.</summary>
public sealed record Finding
{
    public required string RuleId { get; init; }

    public required string SkillId { get; init; }

    public required string Category { get; init; }

    public required string Title { get; init; }

    public required Severity Severity { get; init; }

    public required Basis Basis { get; init; }

    /// <summary>The site or hub URL, or "tenant".</summary>
    public required string Target { get; init; }

    public string? TargetTitle { get; init; }

    public required string Message { get; init; }

    public string? Fix { get; init; }

    public string? Source { get; init; }

    public string? Quote { get; init; }

    public DateOnly? Reviewed { get; init; }
}

/// <summary>How one rule went across all its targets.</summary>
public sealed record RuleResult(RuleDefinition Rule, RuleStatus Status, int Assessed, int Failed, int Unknown)
{
    public string? Error { get; init; }

    /// <summary>Share of assessed targets that passed; null when nothing was assessed.</summary>
    public double? PassRatio => Assessed == 0 ? null : (Assessed - Failed) / (double)Assessed;
}

/// <summary>Runs rules over a snapshot. Deterministic: the same snapshot and rules give the same findings.</summary>
public static partial class RuleEngine
{
    public static IReadOnlyList<RuleResult> Evaluate(
        TenantSnapshot snapshot,
        IEnumerable<RuleDefinition> rules,
        IReadOnlyDictionary<string, RuleOverride>? overrides,
        out IReadOnlyList<Finding> findings)
    {
        var results = new List<RuleResult>();
        var all = new List<Finding>();
        foreach (var original in rules)
        {
            var rule = original;
            var parameters = new Dictionary<string, object?>(rule.Params, StringComparer.OrdinalIgnoreCase);
            if (overrides is not null && overrides.TryGetValue(rule.Id, out var change))
            {
                if (change.Disabled)
                {
                    continue;
                }

                foreach (var (key, value) in change.Params)
                {
                    parameters[key] = value;
                }

                if (change.Severity is { } severity)
                {
                    rule = Copy(rule, severity);
                }
            }

            if (rule.Kind == RuleKind.Advisory || string.IsNullOrWhiteSpace(rule.When))
            {
                results.Add(new RuleResult(rule, RuleStatus.Advisory, 0, 0, 0));
                continue;
            }

            try
            {
                results.Add(Run(snapshot, rule, parameters, all));
            }
            catch (RuleException ex)
            {
                results.Add(new RuleResult(rule, RuleStatus.Error, 0, 0, 0) { Error = $"{rule.Id}: {ex.Message}" });
            }
        }

        findings = all;
        return results;
    }

    private static RuleResult Run(TenantSnapshot snapshot, RuleDefinition rule, Dictionary<string, object?> parameters, List<Finding> findings)
    {
        var condition = Expression.Parse(rule.When!);
        var filter = string.IsNullOrWhiteSpace(rule.Filter) ? null : Expression.Parse(rule.Filter);
        int assessed = 0, failed = 0, unknown = 0, targets = 0;

        foreach (var (context, target, title) in Targets(snapshot, rule.Scope))
        {
            var facts = new SnapshotFacts(rule.Scope, context, parameters);
            if (filter is not null && filter.Test(facts) != true)
            {
                continue;
            }

            targets++;
            switch (condition.Test(facts))
            {
                case null:
                    unknown++;
                    break;
                case true:
                    assessed++;
                    failed++;
                    findings.Add(new Finding
                    {
                        RuleId = rule.Id,
                        SkillId = rule.SkillId,
                        Category = rule.Category,
                        Title = rule.Title,
                        Severity = rule.Severity,
                        Basis = rule.Basis,
                        Target = target,
                        TargetTitle = title,
                        Message = Render(rule.Message ?? rule.Title, facts, parameters),
                        Fix = rule.Fix,
                        Source = rule.Source,
                        Quote = rule.Quote,
                        Reviewed = rule.Reviewed,
                    });
                    break;
                default:
                    assessed++;
                    break;
            }
        }

        var status = targets == 0 ? RuleStatus.NotApplicable
            : assessed == 0 ? RuleStatus.NotAssessed
            : failed > 0 ? RuleStatus.Fail
            : RuleStatus.Pass;
        return new RuleResult(rule, status, assessed, failed, unknown);
    }

    private static IEnumerable<(FactContext Context, string Target, string? Title)> Targets(TenantSnapshot snapshot, RuleScope scope)
    {
        switch (scope)
        {
            case RuleScope.Tenant:
                yield return (new FactContext(snapshot), "tenant", snapshot.Tenant);
                break;
            case RuleScope.Site:
                foreach (var site in snapshot.Sites)
                {
                    yield return (new FactContext(snapshot, Site: site), site.Url, site.Title);
                }

                break;
            case RuleScope.Hub:
                foreach (var hub in snapshot.Hubs)
                {
                    yield return (new FactContext(snapshot, Hub: hub), hub.SiteUrl, hub.Title);
                }

                break;
        }
    }

    [GeneratedRegex(@"\{(\$?[A-Za-z][A-Za-z0-9_.]*)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();

    /// <summary>Fills <c>{fact}</c> and <c>{$param}</c> placeholders; unknown names stay as written.</summary>
    public static string Render(string template, IFactSource facts, IReadOnlyDictionary<string, object?> parameters)
        => Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            try
            {
                var value = name.StartsWith('$')
                    ? parameters.TryGetValue(name[1..], out var p) ? p : null
                    : facts.Get(name);
                return value is null || Expression.IsUnknown(value) ? "?" : Format(value);
            }
            catch (RuleException)
            {
                return match.Value;
            }
        });

    private static string Format(object value) => value switch
    {
        double d => d.ToString(d % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        System.Collections.ICollection c => c.Count.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static RuleDefinition Copy(RuleDefinition rule, Severity severity) => new()
    {
        Id = rule.Id,
        Title = rule.Title,
        Kind = rule.Kind,
        Scope = rule.Scope,
        When = rule.When,
        Filter = rule.Filter,
        Severity = severity,
        Weight = rule.Weight,
        Basis = rule.Basis,
        AppliesTo = rule.AppliesTo,
        Source = rule.Source,
        Quote = rule.Quote,
        Reviewed = rule.Reviewed,
        Message = rule.Message,
        Fix = rule.Fix,
        Params = rule.Params,
        SkillId = rule.SkillId,
        SkillVersion = rule.SkillVersion,
        Category = rule.Category,
    };
}
