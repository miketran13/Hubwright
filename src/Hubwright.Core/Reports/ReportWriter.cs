using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Hubwright.Rules;
using Hubwright.Scoring;
using Hubwright.Snapshot;

namespace Hubwright.Reports;

public enum ReportFormat { Markdown, Html, Json }

/// <summary>
/// Writes an assessment as Markdown, HTML or JSON. Every finding shows its basis (documented or
/// heuristic), source and quote, so a reader can check the claim without trusting Hubwright.
/// </summary>
public static class ReportWriter
{
    public static string Write(Assessment assessment, ReportFormat format, string? narrative = null) => format switch
    {
        ReportFormat.Markdown => Markdown(assessment, narrative),
        ReportFormat.Html => Html(assessment, narrative),
        _ => Json(assessment, narrative),
    };

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    public static string Markdown(Assessment a, string? narrative = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(I($"# Hubwright assessment: {a.Snapshot.Tenant}"));
        sb.AppendLine();
        sb.AppendLine(I($"Snapshot {a.Snapshot.CapturedAt:yyyy-MM-dd HH:mm} UTC ({a.Snapshot.Source.ToString().ToLowerInvariant()}{(a.Snapshot.Scope is null ? "" : ", scope " + a.Snapshot.Scope)}) · {a.Snapshot.Sites.Count} sites · {a.Snapshot.Hubs.Count} hubs · assessed {a.RunAt:yyyy-MM-dd}"));
        sb.AppendLine();
        sb.AppendLine(I($"**Overall: {Score(a.Overall)} ({Assessment.Grade(a.Overall)})** · {a.Findings.Count} findings · {a.Advisories.Count()} things to confirm"));
        sb.AppendLine();
        var unread = SiteAccess.Unread(a.Snapshot);
        if (unread.Count > 0)
        {
            sb.AppendLine(I($"## ⚠ {SiteAccess.Summary(unread)}"));
            sb.AppendLine();
            sb.AppendLine(SiteAccess.Explanation);
            sb.AppendLine();
            sb.AppendLine("| Site | Address | Kind | Not read | Last activity | Storage (MB) |");
            sb.AppendLine("|---|---|---|---|---|---:|");
            foreach (var s in unread)
            {
                sb.AppendLine(I($"| {s.Title.Replace("|", "/", StringComparison.Ordinal)} | {s.Url} | {SiteAccess.Kind(s.Kind)} | {SiteAccess.Missing(s.Level)} | {s.LastActivity:yyyy-MM-dd} | {s.StorageUsedMb} |"));
            }

            sb.AppendLine();
            sb.AppendLine("**To see or get access** ([Active sites](" + SiteAccess.ActiveSitesLink + ") in the SharePoint admin center; [Manage site admins](" + SiteAccess.ManageSiteAdminsDoc + ")):");
            sb.AppendLine();
            foreach (var step in SiteAccess.HowToGetAccess)
            {
                sb.AppendLine("1. " + step);
            }

            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(narrative))
        {
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine(narrative.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("## Scores");
        sb.AppendLine();
        sb.AppendLine("| Category | Score | Grade | Rules | Failed | Not assessed |");
        sb.AppendLine("|---|---:|:---:|---:|---:|---:|");
        foreach (var c in a.Categories)
        {
            sb.AppendLine(I($"| {c.Category} | {Score(c.Score)} | {c.Grade} | {c.Rules} | {c.Failed} | {c.NotAssessed} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();
        if (a.Findings.Count == 0)
        {
            sb.AppendLine("No findings. 🎉");
        }

        foreach (var group in a.Findings.GroupBy(f => f.RuleId))
        {
            var first = group.First();
            sb.AppendLine(I($"### {Icon(first.Severity)} {first.Title} · `{first.RuleId}`"));
            sb.AppendLine();
            sb.AppendLine(I($"{first.Severity} · {first.Category} · {BasisLabel(first.Basis)} · {group.Count()} target(s)"));
            sb.AppendLine();
            foreach (var f in group.Take(25))
            {
                sb.AppendLine(I($"- {f.Message}{(f.Target == "tenant" ? "" : $" ({f.Target})")}"));
            }

            if (group.Count() > 25)
            {
                sb.AppendLine(I($"- …and {group.Count() - 25} more (see the JSON report)."));
            }

            sb.AppendLine();
            if (first.Fix is not null)
            {
                sb.AppendLine(I($"**Fix:** {first.Fix}"));
                sb.AppendLine();
            }

            if (first.Source is not null)
            {
                sb.AppendLine(first.Quote is null ? I($"Source: {first.Source}") : I($"> {first.Quote}\n>\n> — {first.Source} (reviewed {first.Reviewed:yyyy-MM-dd})"));
                sb.AppendLine();
            }
        }

        if (a.Advisories.Any())
        {
            sb.AppendLine("## To confirm");
            sb.AppendLine();
            sb.AppendLine("Hubwright can't verify these from tenant data. They are Microsoft recommendations to check by hand.");
            sb.AppendLine();
            foreach (var r in a.Advisories)
            {
                sb.AppendLine(I($"- **{r.Rule.Title}** (`{r.Rule.Id}`): {r.Rule.Fix}{(r.Rule.Source is null ? "" : $" [source]({r.Rule.Source})")}"));
            }

            sb.AppendLine();
        }

        var notAssessed = a.Results.Where(r => r.Status == RuleStatus.NotAssessed).ToList();
        if (notAssessed.Count > 0 || a.Snapshot.ScanIssues.Count > 0)
        {
            sb.AppendLine("## Not assessed");
            sb.AppendLine();
            sb.AppendLine("These rules had no data to judge. They are left out of the score, never counted as passed.");
            sb.AppendLine();
            foreach (var r in notAssessed)
            {
                sb.AppendLine(I($"- `{r.Rule.Id}` {r.Rule.Title}"));
            }

            foreach (var issue in a.Snapshot.ScanIssues)
            {
                sb.AppendLine(I($"- Scan: {issue.Area}: {issue.Reason}{(issue.Target is null ? "" : $" ({issue.Target})")}"));
            }

            sb.AppendLine();
        }

        foreach (var error in a.Errors)
        {
            sb.AppendLine(I($"> ⚠ Rule error: {error.Error}"));
        }

        sb.AppendLine("---");
        sb.AppendLine(I($"Skills: {string.Join(", ", a.SkillVersions.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"))}"));
        return sb.ToString();
    }

    public static string Html(Assessment a, string? narrative = null)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine(I($"<title>Hubwright · {E(a.Snapshot.Tenant)}</title>"));
        sb.AppendLine("""
            <style>
            :root{--bg:#fbfaf9;--fg:#242424;--muted:#616161;--card:#fff;--line:#e0e0e0;--crit:#c50f1f;--high:#da3b01;--med:#b8860b;--low:#0f6cbd;--info:#616161;--ok:#107c10}
            @media (prefers-color-scheme:dark){:root{--bg:#1b1a19;--fg:#f3f2f1;--muted:#c8c6c4;--card:#252423;--line:#3b3a39}}
            body{font:15px/1.5 "Segoe UI",system-ui,sans-serif;background:var(--bg);color:var(--fg);margin:0}
            main{max-width:1040px;margin:0 auto;padding:32px 20px 64px}
            h1{font-size:28px;margin:0 0 4px} h2{margin-top:36px} .muted{color:var(--muted)}
            .hero{display:flex;gap:24px;align-items:center;flex-wrap:wrap}
            .ring{width:120px;height:120px;border-radius:50%;display:grid;place-items:center;font-size:34px;font-weight:700}
            .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:14px}
            .score{font-size:26px;font-weight:700} details{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:10px 14px;margin:10px 0}
            summary{cursor:pointer;font-weight:600} .sev{font-size:12px;font-weight:700;text-transform:uppercase;padding:2px 8px;border-radius:10px;color:#fff;margin-right:8px}
            .Critical{background:var(--crit)}.High{background:var(--high)}.Medium{background:var(--med)}.Low{background:var(--low)}.Info{background:var(--info)}
            blockquote{border-left:3px solid var(--line);margin:8px 0;padding:2px 12px;color:var(--muted)} .tag{font-size:12px;border:1px solid var(--line);border-radius:10px;padding:1px 8px;margin-left:6px}
            ul{padding-left:20px} a{color:var(--low)}
            .warn{border-left:4px solid var(--med)} table{border-collapse:collapse;width:100%;font-size:14px} th,td{text-align:left;padding:4px 8px;border-bottom:1px solid var(--line);vertical-align:top} td a{word-break:break-all}
            </style></head><body><main>
            """);
        sb.AppendLine(I($"<div class=\"hero\"><div class=\"ring\" style=\"background:conic-gradient({Color(a.Overall)} {(a.Overall ?? 0):0}%, var(--line) 0)\"><div class=\"ring\" style=\"width:96px;height:96px;background:var(--bg)\">{Score(a.Overall)}</div></div>"));
        sb.AppendLine(I($"<div><h1>🏗️ {E(a.Snapshot.Tenant)}</h1><div class=\"muted\">{a.Snapshot.Sites.Count} sites · {a.Snapshot.Hubs.Count} hubs · snapshot {a.Snapshot.CapturedAt:yyyy-MM-dd} · {a.Findings.Count} findings · grade {Assessment.Grade(a.Overall)}</div></div></div>"));
        var unread = SiteAccess.Unread(a.Snapshot);
        if (unread.Count > 0)
        {
            sb.AppendLine(I($"<h2>⚠ {E(SiteAccess.Summary(unread))}</h2><div class=\"card warn\"><p>{E(SiteAccess.Explanation)}</p><table><tr><th>Site</th><th>Address</th><th>Kind</th><th>Not read</th><th>Last activity</th><th>Storage (MB)</th></tr>"));
            foreach (var s in unread)
            {
                sb.AppendLine(I($"<tr><td>{E(s.Title)}</td><td><a href=\"{E(s.Url)}\">{E(s.Url)}</a></td><td>{E(SiteAccess.Kind(s.Kind))}</td><td>{E(SiteAccess.Missing(s.Level))}</td><td>{s.LastActivity:yyyy-MM-dd}</td><td>{s.StorageUsedMb}</td></tr>"));
            }

            sb.AppendLine(I($"</table><p><b>To see or get access</b> (<a href=\"{SiteAccess.ActiveSitesLink}\">Active sites</a> in the SharePoint admin center · <a href=\"{SiteAccess.ManageSiteAdminsDoc}\">Manage site admins</a>):</p><ol>"));
            foreach (var step in SiteAccess.HowToGetAccess)
            {
                sb.AppendLine(I($"<li>{E(step)}</li>"));
            }

            sb.AppendLine("</ol></div>");
        }

        if (!string.IsNullOrWhiteSpace(narrative))
        {
            sb.AppendLine(I($"<h2>Summary</h2><div class=\"card\">{E(narrative).Replace("\n", "<br>", StringComparison.Ordinal)}</div>"));
        }

        sb.AppendLine("<h2>Scores</h2><div class=\"grid\">");
        foreach (var c in a.Categories)
        {
            sb.AppendLine(I($"<div class=\"card\"><div class=\"muted\">{E(c.Category)}</div><div class=\"score\" style=\"color:{Color(c.Score)}\">{Score(c.Score)} <span class=\"muted\" style=\"font-size:15px\">{c.Grade}</span></div><div class=\"muted\">{c.Failed} of {c.Rules} rules failed</div></div>"));
        }

        sb.AppendLine("</div><h2>Findings</h2>");
        foreach (var group in a.Findings.GroupBy(f => f.RuleId))
        {
            var f = group.First();
            sb.AppendLine(I($"<details><summary><span class=\"sev {f.Severity}\">{f.Severity}</span>{E(f.Title)} <span class=\"tag\">{group.Count()}</span><span class=\"tag\">{BasisLabel(f.Basis)}</span></summary><ul>"));
            foreach (var item in group.Take(50))
            {
                sb.AppendLine(I($"<li>{E(item.Message)}{(item.Target == "tenant" ? "" : $" <span class=\"muted\">{E(item.Target)}</span>")}</li>"));
            }

            sb.AppendLine("</ul>");
            if (f.Fix is not null)
            {
                sb.AppendLine(I($"<p><b>Fix:</b> {E(f.Fix)}</p>"));
            }

            if (f.Source is not null)
            {
                sb.AppendLine(I($"<blockquote>{E(f.Quote)}<br>— <a href=\"{E(f.Source)}\">{E(f.Source)}</a> (reviewed {f.Reviewed:yyyy-MM-dd})</blockquote>"));
            }

            sb.AppendLine(I($"<div class=\"muted\"><code>{E(f.RuleId)}</code> · {E(f.SkillId)}</div></details>"));
        }

        if (a.Advisories.Any())
        {
            sb.AppendLine("<h2>To confirm</h2><ul>");
            foreach (var r in a.Advisories)
            {
                sb.AppendLine(I($"<li><b>{E(r.Rule.Title)}</b>: {E(r.Rule.Fix)} {(r.Rule.Source is null ? "" : $"<a href=\"{E(r.Rule.Source)}\">source</a>")}</li>"));
            }

            sb.AppendLine("</ul>");
        }

        sb.AppendLine(I($"<p class=\"muted\">Generated by Hubwright · skills {E(string.Join(", ", a.SkillVersions.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}")))}</p></main></body></html>"));
        return sb.ToString();
    }

    public static string Json(Assessment a, string? narrative = null) => JsonSerializer.Serialize(new
    {
        tenant = a.Snapshot.Tenant,
        capturedAt = a.Snapshot.CapturedAt,
        source = a.Snapshot.Source.ToString().ToLowerInvariant(),
        overall = a.Overall is null ? (double?)null : Math.Round(a.Overall.Value, 1),
        grade = Assessment.Grade(a.Overall),
        narrative,
        categories = a.Categories.Select(c => new { c.Category, score = c.Score is null ? (double?)null : Math.Round(c.Score.Value, 1), c.Grade, c.Rules, c.Failed, c.NotAssessed }),
        findings = a.Findings,
        unreadSites = SiteAccess.Unread(a.Snapshot).Select(s => new { s.Title, s.Url, kind = SiteAccess.Kind(s.Kind), access = s.Level == SiteAccessLevel.CannotOpen ? "cannotOpen" : "cannotReadPermissions", s.LastActivity, s.StorageUsedMb, s.Reason }),
        rules = a.Results.Select(r => new { id = r.Rule.Id, status = r.Status.ToString(), r.Assessed, r.Failed, r.Unknown, r.Error }),
        skills = a.SkillVersions,
    }, Snapshot.TenantSnapshot.Json);

    private static string Score(double? score) => score is null ? "–" : Math.Floor(score.Value).ToString(CultureInfo.InvariantCulture);

    private static string Color(double? score) => score switch
    {
        null => "#8a8886",
        >= 80 => "#107c10",
        >= 60 => "#b8860b",
        _ => "#c50f1f",
    };

    private static string Icon(Severity severity) => severity switch
    {
        Severity.Critical => "🟥",
        Severity.High => "🟧",
        Severity.Medium => "🟨",
        Severity.Low => "🟦",
        _ => "⬜",
    };

    public static string BasisLabel(Basis basis) => basis == Basis.Documented ? "Microsoft-documented" : "heuristic";
}
