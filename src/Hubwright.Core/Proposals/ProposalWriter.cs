using System.Globalization;
using System.Net;
using System.Text;
using Hubwright.Blueprints;
using Hubwright.Preview;
using Hubwright.Rules;
using Hubwright.Templates;

namespace Hubwright.Proposals;

/// <summary>The proposal as a stand-alone HTML page for stakeholders, or Markdown for a wiki or a pull request.</summary>
public static class ProposalWriter
{
    private static string S(double? score) => score is null ? "–" : Math.Floor(score.Value).ToString(CultureInfo.InvariantCulture);

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");

    public static string Markdown(IntranetProposal p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var b = p.Blueprint;
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {p.Name}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Tenant **{p.Tenant}** · scanned {p.SnapshotTaken:yyyy-MM-dd} · approach **{p.Answers.Approach}** · score today **{S(p.ScoreBefore)}**, target design **{S(p.ScoreAfter)}**");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(p.Narrative))
        {
            sb.AppendLine("## Summary").AppendLine().AppendLine(p.Narrative.Trim()).AppendLine();
        }

        sb.AppendLine("## What we found").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"{p.SiteCount} sites, {p.HubCount} hubs, {p.FindingCount} findings, {p.NotAssessed} rules not assessed (data that could not be read).{(p.UnreadSites > 0 ? $" {p.UnreadSites} site(s) could not be read with your access; they are planned from the usage report only." : "")}").AppendLine();
        foreach (var issue in p.TopIssues)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **{issue.Severity}** {issue.Title} ({issue.Count}) · `{issue.RuleId}`");
        }

        sb.AppendLine().AppendLine("## Target structure").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 🏠 **{b.Home.Title}** (home site{(b.Home.ExistingUrl is null ? ", new" : "")})");
        foreach (var hub in b.Hubs)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- 🧭 **{hub.Title}**{(hub.ExistingUrl is null ? " (new)" : " (existing)")}: {hub.Purpose}");
            foreach (var site in hub.Sites)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {(site.Private ? "🔒" : "📄")} {site.Title}{(site.ExistingUrl is null ? " (new)" : " (existing)")}");
            }
        }

        foreach (var site in b.Standalone)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- ⚪ {site.Title} (outside the hubs)");
        }

        sb.AppendLine().AppendLine("## Why this shape").AppendLine();
        foreach (var line in b.Rationale)
        {
            sb.AppendLine("- " + line);
        }

        sb.AppendLine().AppendLine("## Every existing site").AppendLine();
        sb.AppendLine("| Site | Today | Target | Plan | Why |").AppendLine("|---|---|---|---|---|");
        foreach (var s in p.Sites.OrderBy(s => Order(s.Disposition)).ThenBy(s => s.Title, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {Md(s.Title)} | {Md(s.CurrentHub ?? "no hub")} | {Md(s.TargetHub ?? "–")} | {s.Disposition} | {Md(string.Join(" ", s.Reasons))} |");
        }

        sb.AppendLine().AppendLine("## New sites and templates").AppendLine();
        foreach (var n in p.NewSites)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- **{n.Title}**{(n.Hub is null ? "" : $" in {n.Hub}")}: {n.TemplateName ?? "standard site"}{(n.Alternatives.Count > 0 ? $" (or {string.Join(", ", n.Alternatives)})" : "")}. `{n.HowToApply}`");
        }

        sb.AppendLine().AppendLine("## Look and feel").AppendLine();
        foreach (var note in p.ThemeNotes)
        {
            sb.AppendLine("- " + note);
        }

        sb.AppendLine().AppendLine("## Policies").AppendLine();
        foreach (var line in Policies(b))
        {
            sb.AppendLine("- " + line);
        }

        sb.AppendLine().AppendLine("## Roadmap").AppendLine();
        foreach (var phase in p.Phases)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {phase.Name} ({phase.When})").AppendLine().AppendLine(phase.Goal).AppendLine();
            foreach (var item in phase.Items)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- [ ] {item.Text}{(item.Rules.Count > 0 ? $" `{string.Join("`, `", item.Rules)}`" : "")}");
            }

            sb.AppendLine();
        }

        if (p.Decisions.Count > 0)
        {
            sb.AppendLine("## Decisions").AppendLine();
            foreach (var d in p.Decisions)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{d.Question}** {(string.IsNullOrWhiteSpace(d.Answer) ? "_to decide_" : d.Answer)}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Sources").AppendLine();
        foreach (var c in p.Citations)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- <{c.Url}> ({string.Join(", ", c.Rules.Take(8))})");
        }

        sb.AppendLine().AppendLine("_Every count, score and placement comes from Hubwright's rules over the scan. Hubwright changed nothing in the tenant._");
        return sb.ToString();
    }

    public static string Html(IntranetProposal p, IReadOnlyList<SiteTemplate> catalog, Hints.HintBook? hints = null, Snapshot.TenantSnapshot? today = null)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentNullException.ThrowIfNull(catalog);
        hints ??= Hints.HintBook.Empty;
        var b = p.Blueprint;
        var theme = b.Theme();
        var palette = theme.Palette;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>").Append(H(p.Name)).Append("</title><style>");
        sb.Append(CultureInfo.InvariantCulture, $":root{{--p:{palette.GetValueOrDefault("themePrimary")};--d:{palette.GetValueOrDefault("themeDarker")};--l:{palette.GetValueOrDefault("themeLighter")}}}");
        sb.Append("""
            body{margin:0;font:15px/1.5 Segoe UI,system-ui,sans-serif;color:#242424;background:#faf9f8}
            header{background:var(--d);color:#fff;padding:36px 48px 28px}header h1{margin:0 0 6px;font-size:28px}header p{margin:0;opacity:.85}
            nav.toc{position:sticky;top:0;background:#fff;border-bottom:1px solid #edebe9;padding:8px 48px;display:flex;gap:16px;flex-wrap:wrap;font-size:13px;z-index:2}nav.toc a{color:#605e5c;text-decoration:none}nav.toc a:hover{color:var(--p)}
            main{max-width:1180px;margin:0 auto;padding:12px 48px 60px}section{margin-top:30px}h2{font-size:21px;border-bottom:2px solid var(--l);padding-bottom:4px}h3{font-size:16px;margin:18px 0 6px}
            .tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:12px}.tile{background:#fff;border:1px solid #edebe9;border-radius:10px;padding:14px}.tile b{display:block;font-size:30px;color:var(--p)}.tile span{color:#605e5c;font-size:12px}
            .card{background:#fff;border:1px solid #edebe9;border-radius:10px;padding:14px 16px;margin:8px 0}
            .sev{display:inline-block;min-width:62px;text-align:center;font-size:11px;font-weight:600;color:#fff;border-radius:9px;padding:1px 8px;margin-right:6px}.Critical,.High{background:#d13438}.Medium{background:#c97a00}.Low{background:#0f6cbd}.Info{background:#8a8886}
            .tree ul{list-style:none;padding-left:20px;margin:4px 0}.tree>ul{padding-left:0}.tree li{margin:3px 0}.new{font-size:10px;font-weight:600;background:#dff6dd;color:#107c10;border-radius:8px;padding:1px 7px;margin-left:6px}.old{font-size:10px;font-weight:600;background:#f3f2f1;color:#605e5c;border-radius:8px;padding:1px 7px;margin-left:6px}
            table{border-collapse:collapse;width:100%;background:#fff;font-size:13px}th,td{border-bottom:1px solid #edebe9;padding:7px 8px;text-align:left;vertical-align:top}th{background:#f3f2f1;position:sticky;top:38px}
            .d{font-weight:600;border-radius:8px;padding:1px 8px;font-size:11px;white-space:nowrap}.d-keep{background:#dff6dd;color:#107c10}.d-move{background:#deecf9;color:#0f6cbd}.d-modernize{background:#fff4ce;color:#8a5d00}.d-review{background:#f3f2f1;color:#605e5c}.d-archive,.d-retire{background:#fde7e9;color:#a4262c}
            .filters{display:flex;gap:6px;margin:8px 0}.filters button{border:1px solid #e1dfdd;background:#fff;border-radius:14px;padding:3px 12px;cursor:pointer}.filters button.on{background:var(--p);color:#fff;border-color:var(--p)}
            .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:12px}.tcard{background:#fff;border:1px solid #edebe9;border-radius:10px;padding:10px;display:flex;flex-direction:column;gap:6px}.tcard h4{margin:0;font-size:14px}.tcard p{margin:0;font-size:12px;color:#605e5c}.how{font:11px Cascadia Mono,Consolas,monospace;background:#f3f2f1;border-radius:6px;padding:5px;word-break:break-all}
            .swatches{display:flex;flex-wrap:wrap;gap:6px}.sw{width:86px;font-size:10px}.sw i{display:block;height:34px;border-radius:6px;border:1px solid #0001}
            iframe{width:100%;height:760px;border:1px solid #edebe9;border-radius:10px;background:#fff}
            .phase{background:#fff;border-left:4px solid var(--p);border-radius:6px;padding:10px 16px;margin:12px 0}.phase li{margin:4px 0}.rule{font:11px Cascadia Mono,Consolas,monospace;color:#605e5c}
            details.hint{margin:4px 0 6px}details.hint>summary{cursor:pointer;color:var(--p);font-size:12px;list-style:none}details.hint>summary::before{content:"ⓘ ";}details.hint[open]>summary{font-weight:600}
            .hintbox{background:#faf9f8;border:1px solid #edebe9;border-radius:8px;padding:8px 12px;margin-top:4px;font-size:13px}.hintbox h5{margin:6px 0 2px;font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:#605e5c}.hintbox ol{margin:2px 0 2px 18px;padding:0}.hintbox q{display:block;color:#323130;border-left:3px solid var(--l);padding-left:8px;margin:4px 0}
            iframe.structure{height:680px}iframe.prototype{height:820px}.logo-preview{max-height:64px;max-width:192px;border:1px dashed #c8c6c4;border-radius:6px;padding:4px;background:#fff}
            .muted{color:#605e5c;font-size:13px}footer{color:#605e5c;font-size:12px;margin-top:40px}
            @media print{nav.toc{display:none}iframe{display:none}th{position:static}}
            """);
        sb.Append(TemplateGallery.Css).Append("</style></head><body>");
        sb.Append("<header><h1>").Append(H(p.Name)).Append("</h1><p>").Append(H($"Tenant {p.Tenant} · scanned {p.SnapshotTaken:yyyy-MM-dd} · approach: {Approach(p.Answers.Approach)} · prepared with Hubwright {p.Created:yyyy-MM-dd}")).Append("</p></header>");
        sb.Append("<nav class=\"toc\"><a href=\"#summary\">Summary</a><a href=\"#found\">What we found</a><a href=\"#structure\">Structure</a><a href=\"#walk\">Walk through</a><a href=\"#sites\">Every site</a><a href=\"#new\">New sites</a><a href=\"#look\">Look and feel</a><a href=\"#policies\">Policies</a><a href=\"#roadmap\">Roadmap</a><a href=\"#decisions\">Decisions</a><a href=\"#sources\">Sources</a></nav><main>");

        // Summary
        sb.Append("<section id=\"summary\"><h2>Summary</h2><div class=\"tiles\">");
        Tile(sb, S(p.ScoreBefore), "score today");
        Tile(sb, S(p.ScoreAfter), "target design, checked by the same rules");
        Tile(sb, (b.Hubs.Count).ToString(CultureInfo.InvariantCulture), $"hubs ({b.Hubs.Count(h => h.ExistingUrl is not null)} kept)");
        Tile(sb, p.NewSites.Count.ToString(CultureInfo.InvariantCulture), "new sites from templates");
        Tile(sb, p.Sites.Count(s => s.Disposition is "archive" or "retire").ToString(CultureInfo.InvariantCulture), "sites to retire or archive");
        sb.Append("</div>");
        if (!string.IsNullOrWhiteSpace(p.Narrative))
        {
            sb.Append("<div class=\"card\">");
            foreach (var para in p.Narrative.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append("<p>").Append(H(para.Trim()).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>");
            }

            sb.Append("<p class=\"muted\">Written by Hubwright's AI architect from the numbers below.</p></div>");
        }

        sb.Append("<div class=\"card\"><b>Why this shape</b><ul>");
        foreach (var line in b.Rationale)
        {
            sb.Append("<li>").Append(H(line)).Append("</li>");
        }

        sb.Append("</ul></div></section>");

        // What we found
        sb.Append("<section id=\"found\"><h2>What we found</h2><p>").Append(H($"{p.SiteCount} sites, {p.HubCount} hubs and {p.FindingCount} findings. {p.NotAssessed} rule(s) could not be assessed because the scan could not read their data; they are not counted as passing.{(p.UnreadSites > 0 ? $" {p.UnreadSites} site(s) could not be read with your access (you are not a member); they are planned from the usage report only." : "")}")).Append("</p>");
        if (p.Categories.Count > 0)
        {
            sb.Append("<table><tr><th>Area</th><th>Today</th><th>Target design</th></tr>");
            foreach (var c in p.Categories)
            {
                sb.Append("<tr><td>").Append(H(c.Category)).Append("</td><td>").Append(S(c.Before)).Append("</td><td>").Append(S(c.After)).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        foreach (var i in p.TopIssues)
        {
            sb.Append("<div class=\"card\"><span class=\"sev ").Append(i.Severity).Append("\">").Append(i.Severity).Append("</span><b>").Append(H(i.Title)).Append("</b> <span class=\"muted\">").Append(H($"{i.Count} · {(i.Basis == Basis.Documented ? "Microsoft-documented" : "heuristic")}")).Append("</span> <span class=\"rule\">").Append(H(i.RuleId)).Append("</span>");
            if (i.Fix is not null)
            {
                sb.Append("<div>").Append(H(i.Fix)).Append("</div>");
            }

            if (i.Quote is not null)
            {
                sb.Append("<div class=\"muted\">“").Append(H(i.Quote)).Append("” <a href=\"").Append(H(i.Source)).Append("\">source</a></div>");
            }

            sb.Append("</div>");
        }

        sb.Append("</section>");

        // Structure
        sb.Append("<section id=\"structure\"><h2>Target structure</h2><p class=\"muted\">Select a site to see where it is today, where it goes and why. Switch between the target, today next to the target, and the list of moves.</p>");
        sb.Append("<iframe class=\"structure\" title=\"Structure preview\" srcdoc=\"").Append(H(StructurePreview.Render(p, today, hints))).Append("\"></iframe>");
        sb.Append("<h3 id=\"walk\">Walk through the new intranet</h3><p class=\"muted\">Every site as a page with sample content, in the new theme and navigation. Use the app bar, hub menus and breadcrumbs; ← and → go back and forward. From the structure above, select a site and Open in site preview.</p>");
        sb.Append("<iframe class=\"prototype\" title=\"Site preview\" srcdoc=\"").Append(H(SitePrototype.Render(p, catalog, b.Logo))).Append("\"></iframe>");
        sb.Append("<h3>As a list</h3><p class=\"muted\"><span class=\"new\">new</span> is created, <span class=\"old\">existing</span> is kept.</p><div class=\"card tree\"><ul>");
        sb.Append("<li>🏠 <b>").Append(H(b.Home.Title)).Append("</b> home site").Append(Badge(b.Home.ExistingUrl)).Append("</li>");
        foreach (var hub in b.Hubs)
        {
            sb.Append("<li>🧭 <b>").Append(H(hub.Title)).Append("</b>").Append(Badge(hub.ExistingUrl)).Append(" <span class=\"muted\">").Append(H(hub.Purpose)).Append("</span><ul>");
            foreach (var site in hub.Sites)
            {
                sb.Append("<li>").Append(site.Private ? "🔒 " : "📄 ").Append(H(site.Title)).Append(Badge(site.ExistingUrl)).Append("</li>");
            }

            var moved = p.Sites.Where(s => s.TargetHub == hub.Title && !hub.Sites.Any(x => x.ExistingUrl == s.Url) && s.Url != hub.ExistingUrl).ToList();
            foreach (var s in moved.Take(30))
            {
                sb.Append("<li>").Append(s.Kind == Snapshot.SiteKind.Communication ? "📄 " : "👥 ").Append(H(s.Title)).Append(Badge(s.Url)).Append("</li>");
            }

            if (moved.Count > 30)
            {
                sb.Append("<li class=\"muted\">").Append(moved.Count - 30).Append(" more</li>");
            }

            sb.Append("</ul></li>");
        }

        foreach (var site in b.Standalone)
        {
            sb.Append("<li>⚪ ").Append(H(site.Title)).Append(" <span class=\"muted\">outside the hubs</span>").Append(Badge(site.ExistingUrl)).Append("</li>");
        }

        sb.Append("</ul></div><h3>Global navigation (app bar)</h3><div class=\"card\"><ul>");
        foreach (var node in b.GlobalNavigation)
        {
            sb.Append("<li>").Append(H(node.Title));
            if (node.Children.Count > 0)
            {
                sb.Append(": ").Append(H(string.Join(" · ", node.Children.Select(c => c.Title))));
            }

            sb.Append("</li>");
        }

        sb.Append("</ul></div></section>");

        // Every site
        sb.Append("<section id=\"sites\"><h2>Every existing site</h2><div class=\"filters\" id=\"f\"><button class=\"on\" data-d=\"all\">All ").Append(p.Sites.Count).Append("</button>");
        foreach (var g in p.Sites.GroupBy(s => s.Disposition).OrderBy(g => Order(g.Key)))
        {
            sb.Append("<button data-d=\"").Append(H(g.Key)).Append("\">").Append(H(g.Key)).Append(' ').Append(g.Count()).Append("</button>");
        }

        sb.Append("</div><table id=\"t\"><tr><th>Site</th><th>Today</th><th>Target</th><th>Plan</th><th>Why</th></tr>");
        foreach (var s in p.Sites.OrderBy(s => Order(s.Disposition)).ThenBy(s => s.Title, StringComparer.Ordinal))
        {
            sb.Append("<tr data-d=\"").Append(H(s.Disposition)).Append("\"><td><a href=\"").Append(H(s.Url)).Append("\">").Append(H(s.Title)).Append("</a></td><td>").Append(H(s.CurrentHub ?? "no hub")).Append("</td><td>").Append(H(s.TargetHub ?? "–")).Append("</td><td><span class=\"d d-").Append(H(s.Disposition)).Append("\">").Append(H(s.Disposition)).Append("</span></td><td>").Append(H(string.Join(" ", s.Reasons))).Append(HintList(s.Actions.Distinct().Select(hints.For).OfType<Hints.Hint>(), hints)).Append("</td></tr>");
        }

        sb.Append("</table></section>");

        // New sites
        var byId = catalog.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        sb.Append("<section id=\"new\"><h2>New sites and templates</h2><p class=\"muted\">Each new site starts from a ready-made SharePoint template: built into SharePoint (Settings → Apply a site template) or a look book PnP package. ").Append(H(TemplateGallery.Disclaimer)).Append("</p><div class=\"grid\">");
        foreach (var n in p.NewSites)
        {
            sb.Append("<div class=\"tcard\">");
            if (n.TemplateId is not null && byId.TryGetValue(n.TemplateId, out var template))
            {
                sb.Append(TemplateGallery.Sketch(template, theme));
            }

            sb.Append("<h4>").Append(H(n.Title)).Append("</h4><p>").Append(H(n.Hub is null ? n.Purpose : $"In {n.Hub}. {n.Purpose}")).Append("</p><p><b>").Append(H(n.TemplateName ?? "Standard site")).Append("</b>").Append(H(n.Alternatives.Count > 0 ? $" · or {string.Join(", ", n.Alternatives)}" : "")).Append("</p><div class=\"how\">").Append(H(n.HowToApply)).Append("</div></div>");
        }

        sb.Append("</div></section>");

        // Look and feel
        sb.Append("<section id=\"look\"><h2>Look and feel</h2><div class=\"card\"><ul>");
        foreach (var note in p.ThemeNotes)
        {
            sb.Append("<li>").Append(H(note)).Append("</li>");
        }

        sb.Append("</ul>");
        if (b.Logo is not null)
        {
            sb.Append("<p>Site logo: <img class=\"logo-preview\" alt=\"logo\" src=\"").Append(H(b.Logo)).Append("\"></p>");
        }
        else
        {
            sb.Append("<p class=\"muted\">No logo yet: add one in Hubwright's theme editor. Microsoft asks for a logo that fits within 64 x 64 to 192 x 64 pixels, and a square thumbnail.</p>");
        }

        sb.Append("<div class=\"swatches\">");
        foreach (var key in new[] { "themePrimary", "themeDarkAlt", "themeDark", "themeDarker", "themeSecondary", "themeTertiary", "themeLight", "themeLighter", "themeLighterAlt", "neutralPrimary", "white" })
        {
            if (palette.TryGetValue(key, out var hex))
            {
                sb.Append("<div class=\"sw\"><i style=\"background:").Append(H(hex)).Append("\"></i>").Append(H(key)).Append("<br>").Append(H(hex)).Append("</div>");
            }
        }

        sb.Append("</div></div><p class=\"muted\">The home page mockup below is interactive: switch header layouts, open the mega menu, try mobile width.</p>");
        sb.Append("<iframe title=\"Home page mockup\" srcdoc=\"").Append(H(MockupRenderer.Render(MockupModel.FromBlueprint(b)))).Append("\"></iframe></section>");

        // Policies
        sb.Append("<section id=\"policies\"><h2>Policies</h2><div class=\"card\"><ul>");
        foreach (var line in Policies(b))
        {
            sb.Append("<li>").Append(H(line)).Append("</li>");
        }

        sb.Append("</ul></div></section>");

        // Roadmap
        sb.Append("<section id=\"roadmap\"><h2>Roadmap</h2>");
        foreach (var phase in p.Phases)
        {
            sb.Append("<div class=\"phase\"><h3>").Append(H(phase.Name)).Append(" <span class=\"muted\">").Append(H(phase.When)).Append("</span></h3><div class=\"muted\">").Append(H(phase.Goal)).Append("</div><ul>");
            foreach (var item in phase.Items)
            {
                sb.Append("<li>").Append(H(item.Text));
                if (item.Rules.Count > 0)
                {
                    sb.Append(" <span class=\"rule\">").Append(H(string.Join(", ", item.Rules))).Append("</span>");
                }

                if (hints.For(item) is { } hint)
                {
                    sb.Append(HintDetails(hint, hints, "What, why, how"));
                }

                sb.Append("</li>");
            }

            sb.Append("</ul></div>");
        }

        sb.Append("</section>");

        // Decisions
        sb.Append("<section id=\"decisions\"><h2>Decisions</h2>");
        if (p.Decisions.Count == 0)
        {
            sb.Append("<p class=\"muted\">None recorded.</p>");
        }

        foreach (var d in p.Decisions)
        {
            sb.Append("<div class=\"card\"><b>").Append(H(d.Question)).Append("</b><div>").Append(string.IsNullOrWhiteSpace(d.Answer) ? "<i>To decide.</i>" : H(d.Answer)).Append("</div>");
            if (d.Why is not null)
            {
                sb.Append("<div class=\"muted\">").Append(H(d.Why)).Append("</div>");
            }

            sb.Append("</div>");
        }

        sb.Append("</section>");

        // Sources
        sb.Append("<section id=\"sources\"><h2>Sources</h2><p class=\"muted\">Microsoft-documented rules quote the page they enforce. Heuristics are Hubwright's judgment, with thresholds you can tune.</p>");
        foreach (var c in p.Citations)
        {
            sb.Append("<div class=\"card\"><a href=\"").Append(H(c.Url)).Append("\">").Append(H(c.Url)).Append("</a>");
            foreach (var q in c.Quotes)
            {
                sb.Append("<div class=\"muted\">“").Append(H(q)).Append("”</div>");
            }

            sb.Append("<div class=\"rule\">").Append(H(string.Join(", ", c.Rules))).Append("</div></div>");
        }

        sb.Append("</section><footer>Every count, score and site placement comes from Hubwright's rules over the scan; nothing was changed in the tenant. Hubwright is not affiliated with Microsoft.</footer></main>");
        sb.Append("<script>window.addEventListener('message',e=>{if(e.data&&e.data.hubwright==='open'){const f=document.querySelector('iframe.prototype');f.contentWindow.postMessage(e.data,'*');f.scrollIntoView({behavior:'smooth'})}});</script>");
        sb.Append("<script>document.querySelectorAll('#f button').forEach(b=>b.onclick=()=>{document.querySelectorAll('#f button').forEach(x=>x.classList.toggle('on',x===b));document.querySelectorAll('#t tr[data-d]').forEach(r=>r.style.display=b.dataset.d==='all'||r.dataset.d===b.dataset.d?'':'none')})</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>A collapsible "what, why, how, example" for one hint, with the quotes behind it.</summary>
    public static string HintDetails(Hints.Hint hint, Hints.HintBook book, string summary)
    {
        ArgumentNullException.ThrowIfNull(hint);
        ArgumentNullException.ThrowIfNull(book);
        var sb = new StringBuilder("<details class=\"hint\"><summary>");
        sb.Append(H(summary)).Append("</summary><div class=\"hintbox\"><h5>What</h5>").Append(H(hint.What)).Append("<h5>Why</h5>").Append(H(hint.Why));
        foreach (var e in book.Evidence(hint))
        {
            sb.Append("<q>").Append(H(e.Quote)).Append(e.Source.Length > 0 ? $" <a href=\"{H(e.Source)}\">{H(e.Label)}</a>" : "").Append("</q>");
        }

        if (hint.How.Count > 0)
        {
            sb.Append("<h5>How</h5><ol>");
            foreach (var step in hint.How)
            {
                sb.Append("<li>").Append(H(step)).Append("</li>");
            }

            sb.Append("</ol>");
        }

        if (hint.Example is not null)
        {
            sb.Append("<h5>Example</h5>").Append(H(hint.Example));
        }

        return sb.Append("</div></details>").ToString();
    }

    private static string HintList(IEnumerable<Hints.Hint> hints, Hints.HintBook book)
    {
        var list = hints.DistinctBy(h => h.Id).ToList();
        return list.Count == 0 ? "" : string.Concat(list.Select(h => HintDetails(h, book, h.Title)));
    }

    public static IEnumerable<string> Policies(Blueprint b)
    {
        ArgumentNullException.ThrowIfNull(b);
        var p = b.Policy;
        yield return $"External sharing: {IntranetProposalGenerator.Human(p.Sharing)}; default link: people in the organization.";
        if (p.Sharing == Snapshot.SharingLevel.Anyone)
        {
            yield return $"Anyone links: view-only, expire after {p.AnyoneLinkExpirationDays} days.";
        }

        if (p.Sharing != Snapshot.SharingLevel.OnlyOrganization)
        {
            yield return $"Guest access expires after {p.GuestAccessExpirationDays} days.";
        }

        yield return $"Every site has at least {p.MinimumOwners} owners.";
        yield return $"Inactive sites: owners are asked after {p.InactiveMonths} months; site ownership and attestation policies are on as well.";
        yield return "Sensitivity labels: General, Confidential for private team sites, Partner for extranets.";
        yield return $"Naming: {p.NamingConvention}.";
    }

    private static void Tile(StringBuilder sb, string value, string label)
        => sb.Append("<div class=\"tile\"><b>").Append(H(value)).Append("</b><span>").Append(H(label)).Append("</span></div>");

    private static string Badge(string? existing) => existing is null ? "<span class=\"new\">new</span>" : "<span class=\"old\">existing</span>";

    private static string Approach(ProposalApproach approach) => approach switch
    {
        ProposalApproach.KeepWhatWorks => "keep what works",
        ProposalApproach.Restructure => "restructure",
        _ => "balanced",
    };

    private static int Order(string disposition) => disposition switch
    {
        "keep" => 0,
        "move" => 1,
        "modernize" => 2,
        "review" => 3,
        "archive" => 4,
        "retire" => 5,
        _ => 6,
    };

    private static string Md(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
