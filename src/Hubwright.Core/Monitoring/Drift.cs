using System.Globalization;
using System.Text;
using Hubwright.Blueprints;
using Hubwright.Implementation;
using Hubwright.Rules;
using Hubwright.Scoring;
using Hubwright.Security;
using Hubwright.Snapshot;

namespace Hubwright.Monitoring;

public enum DriftState
{
    /// <summary>As planned.</summary>
    InPlace,

    /// <summary>Built (or set) once, different now: someone or something changed it.</summary>
    Drifted,

    /// <summary>Not like the plan, and never built: still to do, not drift.</summary>
    NotBuilt,

    /// <summary>The scan couldn't read it. Not a pass.</summary>
    NotRead,
}

/// <summary>One thing the plan or the baseline expects, against the latest scan.</summary>
public sealed record DriftItem(string Area, string Title, string Expected, string? Actual, DriftState State);

/// <summary>
/// Compares the latest scan with the intranet plan (home site, hubs, hub membership) and the
/// security baseline. A difference counts as drift only when the build or baseline step for it
/// finished; otherwise it is work not done yet.
/// </summary>
public static class Drift
{
    public static IReadOnlyList<DriftItem> Check(TenantSnapshot snapshot, Blueprint? plan, ImplementationPlan? build, IEnumerable<BaselineSetting> baseline, ImplementationPlan? applied)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(baseline);
        var items = new List<DriftItem>();
        bool Finished(ImplementationPlan? p, string id) => p?.Find(id) is { IsFinished: true } s && s.Status != StepStatus.Skipped;
        DriftState State(bool same, bool built) => same ? DriftState.InPlace : built ? DriftState.Drifted : DriftState.NotBuilt;

        if (plan is not null)
        {
            var home = plan.Home.ExistingUrl ?? plan.Url(plan.Home.Slug);
            var actualHome = snapshot.HomeSite?.Url;
            items.Add(new("Structure", "Home site", home, actualHome, Same(home, actualHome) ? DriftState.InPlace : State(false, Finished(build, "set-home-site"))));

            foreach (var hub in plan.Hubs)
            {
                var url = hub.ExistingUrl ?? plan.Url(hub.Slug);
                var found = snapshot.Hubs.FirstOrDefault(h => Same(h.SiteUrl, url));
                var built = Finished(build, "register-" + hub.Slug) || Finished(build, "hub-" + hub.Slug) || hub.ExistingUrl is not null;
                items.Add(new("Hubs", $"Hub \"{hub.Title}\"", url, found is null ? "not a hub" : $"hub \"{found.Title}\"", State(found is not null, built)));
                if (found is null)
                {
                    continue;
                }

                foreach (var site in hub.Sites)
                {
                    var siteUrl = site.ExistingUrl ?? plan.Url(site.Slug);
                    var actual = snapshot.Sites.FirstOrDefault(s => Same(s.Url, siteUrl));
                    if (actual is null)
                    {
                        continue;
                    }

                    var inHub = string.Equals(actual.HubId, found.Id, StringComparison.OrdinalIgnoreCase);
                    var actualHub = actual.HubId is null ? "in no hub" : snapshot.Hubs.FirstOrDefault(h => string.Equals(h.Id, actual.HubId, StringComparison.OrdinalIgnoreCase))?.Title ?? "another hub";
                    items.Add(new("Hubs", $"\"{site.Title}\" in \"{hub.Title}\"", hub.Title, inHub ? hub.Title : actualHub, State(inHub, build?.Steps.Any(s => s.Target is { } t && Same(t, siteUrl) && s.Operation == StepOperation.AssociateWithHub && s.IsFinished) ?? false)));
                }
            }
        }

        if (applied is not null)
        {
            foreach (var setting in baseline.Where(b => b.Fact is not null))
            {
                if (applied.Find("baseline-" + setting.Id) is not { } step || !step.Parameters.TryGetValue("value", out var target))
                {
                    continue;
                }

                var current = SecurityBaseline.Current(snapshot.Settings, setting.Fact);
                var state = current is null ? DriftState.NotRead : State(SecurityBaseline.Same(current, target, setting), step.IsFinished && step.Status != StepStatus.Skipped);
                items.Add(new("Security", setting.Title, SecurityBaseline.Human(target), current is null ? null : SecurityBaseline.Human(current), state));
            }
        }

        return items;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sites by owner count, for an attestation round with site owners.</summary>
public static class OwnerReport
{
    public sealed record Row(string Title, string Url, int? Owners, string? Hub, DateTimeOffset? LastActivity, string Status);

    /// <param name="minimum">Hubwright's default is two owners (the blueprint's policy), so someone can cover absences.</param>
    public static IReadOnlyList<Row> For(TenantSnapshot snapshot, int minimum = 2)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. snapshot.Sites
            .Where(s => s.OwnerCount is null || s.OwnerCount < minimum)
            .Select(s => new Row(s.Title, s.Url, s.OwnerCount, s.HubId is null ? null : snapshot.Hubs.FirstOrDefault(h => string.Equals(h.Id, s.HubId, StringComparison.OrdinalIgnoreCase))?.Title, s.LastActivity,
                s.OwnerCount switch { null => "not read", 0 => "no owner", _ => $"{s.OwnerCount} owner(s)" }))
            .OrderBy(r => r.Owners ?? -1).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)];
    }
}

/// <summary>CSV for Excel: findings and owner lists. UTF-8 with a BOM so Excel reads accents.</summary>
public static class CsvExport
{
    public static string Findings(Assessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var sb = new StringBuilder("﻿");
        Line(sb, "Severity", "Category", "Rule", "Title", "Target", "Message", "Fix", "Basis", "Source");
        foreach (var f in assessment.Findings)
        {
            Line(sb, f.Severity.ToString(), f.Category, f.RuleId, f.Title, f.Target, f.Message, f.Fix ?? "", f.Basis.ToString(), f.Source ?? "");
        }

        return sb.ToString();
    }

    public static string Owners(IEnumerable<OwnerReport.Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder("﻿");
        Line(sb, "Site", "URL", "Owners", "Status", "Hub", "Last activity", "Confirmed owner (to fill in)");
        foreach (var r in rows)
        {
            Line(sb, r.Title, r.Url, r.Owners?.ToString(CultureInfo.InvariantCulture) ?? "", r.Status, r.Hub ?? "", r.LastActivity?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", "");
        }

        return sb.ToString();
    }

    public static string Drift(IEnumerable<DriftItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var sb = new StringBuilder("﻿");
        Line(sb, "Area", "Item", "Expected", "Actual", "State");
        foreach (var d in items)
        {
            Line(sb, d.Area, d.Title, d.Expected, d.Actual ?? "not read", d.State.ToString());
        }

        return sb.ToString();
    }

    private static void Line(StringBuilder sb, params string[] cells)
        => sb.Append(string.Join(",", cells.Select(Cell))).Append("\r\n");

    /// <summary>Quotes every cell, and defuses formulas so a site title like "=HYPERLINK(...)" stays text in Excel.</summary>
    private static string Cell(string value)
    {
        var text = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

/// <summary>A short status for Teams or email: grade, journey, drift and the top findings.</summary>
public static class Summary
{
    public static string Markdown(string tenant, Assessment assessment, IEnumerable<(string Title, string State, string Status)> stages, IReadOnlyList<DriftItem> drift)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(drift);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Intranet status: {tenant}** · grade {Assessment.Grade(assessment.Overall)} · scan of {assessment.Snapshot.CapturedAt:yyyy-MM-dd}");
        sb.AppendLine();
        foreach (var (title, state, status) in stages)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {state} **{title}**: {status}");
        }

        var drifted = drift.Count(d => d.State == DriftState.Drifted);
        sb.AppendLine();
        sb.AppendLine(drifted == 0 ? "No drift from the plan or the baseline." : $"**{drifted} item(s) drifted** from the plan or the baseline since they were built.");
        sb.AppendLine();
        sb.AppendLine("Top findings:");
        foreach (var group in assessment.Findings.Where(f => f.Severity >= Severity.High).GroupBy(f => f.RuleId).Take(5))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {group.First().Severity}: {group.First().Title} ({group.Count()})");
        }

        return sb.ToString();
    }
}
