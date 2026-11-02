using System.Globalization;
using System.Text;
using Hubwright.Scoring;

namespace Hubwright.Engine;

/// <summary>What changed between two assessments of the same tenant: scores, sites, findings.</summary>
public sealed record AssessmentDiff(
    double? OverallBefore,
    double? OverallAfter,
    IReadOnlyList<(string Category, double? Before, double? After)> Categories,
    IReadOnlyList<string> SitesAdded,
    IReadOnlyList<string> SitesRemoved,
    IReadOnlyList<string> NewFindings,
    IReadOnlyList<string> ResolvedFindings)
{
    public static AssessmentDiff Between(Assessment before, Assessment after)
    {
        static string Key(Rules.Finding f) => $"{f.RuleId} · {f.TargetTitle ?? f.Target}";
        var beforeSites = before.Snapshot.Sites.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var afterSites = after.Snapshot.Sites.Select(s => s.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var beforeFindings = before.Findings.Select(Key).ToHashSet(StringComparer.Ordinal);
        var afterFindings = after.Findings.Select(Key).ToHashSet(StringComparer.Ordinal);
        var categories = before.Categories.Select(c => c.Category).Union(after.Categories.Select(c => c.Category), StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(c => (c, before.Categories.FirstOrDefault(x => x.Category == c)?.Score, after.Categories.FirstOrDefault(x => x.Category == c)?.Score))
            .ToList();
        return new AssessmentDiff(
            before.Overall,
            after.Overall,
            categories,
            [.. afterSites.Except(beforeSites).Order(StringComparer.Ordinal)],
            [.. beforeSites.Except(afterSites).Order(StringComparer.Ordinal)],
            [.. afterFindings.Except(beforeFindings).Order(StringComparer.Ordinal)],
            [.. beforeFindings.Except(afterFindings).Order(StringComparer.Ordinal)]);
    }

    public string ToMarkdown()
    {
        static string S(double? v) => v is null ? "–" : Math.Round(v.Value).ToString(CultureInfo.InvariantCulture);
        static string Delta(double? a, double? b) => a is null || b is null ? "" : (b - a) switch { > 0.5 => $" ▲{S(b - a)}", < -0.5 => $" ▼{S(a - b)}", _ => "" };
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Changes: overall {S(OverallBefore)} → {S(OverallAfter)}{Delta(OverallBefore, OverallAfter)}");
        sb.AppendLine();
        foreach (var (category, before, after) in Categories)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {category}: {S(before)} → {S(after)}{Delta(before, after)}");
        }

        void List(string title, IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {title} ({items.Count})");
            foreach (var item in items.Take(60))
            {
                sb.AppendLine("- " + item);
            }
        }

        List("Resolved findings", ResolvedFindings);
        List("New findings", NewFindings);
        List("Sites added", SitesAdded);
        List("Sites removed", SitesRemoved);
        return sb.ToString();
    }
}
