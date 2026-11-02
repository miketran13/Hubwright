using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Hubwright.Blueprints;
using Hubwright.Snapshot;
using Hubwright.Theming;

namespace Hubwright.Preview;

/// <summary>One hub and its sites, for the information architecture tab.</summary>
public sealed record MockupHub(string Title, IReadOnlyList<string> Sites, string? Parent = null);

/// <summary>Everything the page mockup shows. Built from a blueprint or a snapshot.</summary>
public sealed class MockupModel
{
    public string Organization { get; init; } = "Contoso";

    public string SiteTitle { get; init; } = "Contoso Home";

    public string HubTitle { get; init; } = "Contoso";

    public required SharePointTheme Theme { get; init; }

    /// <summary>minimal, compact, standard or extended.</summary>
    public string HeaderLayout { get; init; } = "standard";

    public IReadOnlyList<NavNode> GlobalNavigation { get; init; } = [];

    public IReadOnlyList<NavNode> HubNavigation { get; init; } = [];

    public IReadOnlyList<string> QuickLinks { get; init; } = [];

    public IReadOnlyList<MockupHub> Hubs { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The site logo: a data: URI, or a path relative to the page. Initials when empty.</summary>
    public string? Logo { get; init; }

    public static MockupModel FromBlueprint(Blueprint blueprint)
    {
        var hub = blueprint.Hubs.FirstOrDefault();
        return new MockupModel
        {
            Organization = blueprint.Organization,
            SiteTitle = blueprint.Home.Title,
            HubTitle = blueprint.Organization,
            Theme = blueprint.Theme(),
            HeaderLayout = blueprint.HeaderLayout,
            GlobalNavigation = blueprint.GlobalNavigation,
            HubNavigation = blueprint.GlobalNavigation.Where(n => n.Title != "Home").ToList() is { Count: > 0 } top ? [new NavNode { Title = "Home" }, .. top] : hub?.Navigation ?? [],
            QuickLinks = [.. blueprint.Hubs.Select(h => h.Title.Replace(" Hub", "", StringComparison.Ordinal)).Take(8)],
            Hubs = [.. blueprint.Hubs.Select(h => new MockupHub(h.Title, [.. h.Sites.Select(s => s.Title + (s.Private ? " 🔒" : ""))], h.Parent))],
            Notes = blueprint.Rationale,
            Logo = blueprint.Logo,
        };
    }

    /// <summary>The same page in another theme (an imported theme file, or another background).</summary>
    public MockupModel WithTheme(SharePointTheme theme) => new()
    {
        Organization = Organization,
        SiteTitle = SiteTitle,
        HubTitle = HubTitle,
        Theme = theme,
        HeaderLayout = HeaderLayout,
        GlobalNavigation = GlobalNavigation,
        HubNavigation = HubNavigation,
        QuickLinks = QuickLinks,
        Hubs = Hubs,
        Notes = Notes,
        Logo = Logo,
    };

    /// <summary>The same page with a site logo.</summary>
    public MockupModel WithLogo(string? logo) => new()
    {
        Organization = Organization,
        SiteTitle = SiteTitle,
        HubTitle = HubTitle,
        Theme = Theme,
        HeaderLayout = HeaderLayout,
        GlobalNavigation = GlobalNavigation,
        HubNavigation = HubNavigation,
        QuickLinks = QuickLinks,
        Hubs = Hubs,
        Notes = Notes,
        Logo = logo,
    };

    public static MockupModel FromSnapshot(TenantSnapshot snapshot, SharePointTheme theme, string headerLayout)
    {
        var home = snapshot.HomeSite;
        var hub = snapshot.Hubs.FirstOrDefault();
        return new MockupModel
        {
            Organization = snapshot.Tenant,
            SiteTitle = home?.Title ?? hub?.Title ?? snapshot.Tenant,
            HubTitle = hub?.Title ?? snapshot.Tenant,
            Theme = theme,
            HeaderLayout = headerLayout,
            GlobalNavigation = snapshot.GlobalNavigation,
            HubNavigation = hub?.Navigation ?? home?.Navigation ?? [],
            QuickLinks = [.. snapshot.Hubs.Select(h => h.Title).Take(8)],
            Hubs = [.. snapshot.Hubs.Select(h => new MockupHub(h.Title, [.. snapshot.Sites.Where(s => s.HubId == h.Id && !s.IsHub).Select(s => s.Title)], snapshot.Hubs.FirstOrDefault(p => p.Id == h.ParentHubId)?.Title))],
        };
    }
}

/// <summary>
/// Renders a stand-alone HTML mockup of a modern intranet home page in a theme: app bar, hub
/// navigation with a mega menu, the four header layouts, hero, news, quick links, events and footer.
/// It is a generic look-alike for stakeholder review. It loads nothing from the network and embeds
/// no SharePoint code.
/// </summary>
public static class MockupRenderer
{
    public static string Render(MockupModel model)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var p = model.Theme.Palette;

        var css = new StringBuilder(":root{");
        foreach (var (slot, value) in p)
        {
            css.Append(CultureInfo.InvariantCulture, $"--{slot}:{value};");
        }

        css.Append('}');
        var theme = JsonSerializer.Serialize(p);

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $$"""
            <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>{{E(model.SiteTitle)}} · Hubwright preview</title>
            <style>
            {{css}}
            *{box-sizing:border-box} body{margin:0;font:14px/1.45 "Segoe UI",system-ui,sans-serif;background:#e9e9e9;color:var(--neutralPrimary)}
            .toolbar{position:sticky;top:0;z-index:20;background:#1b1a19;color:#fff;display:flex;gap:8px;align-items:center;padding:8px 14px;flex-wrap:wrap}
            .toolbar b{margin-right:8px} .toolbar button{background:#3b3a39;color:#fff;border:0;border-radius:4px;padding:5px 10px;cursor:pointer}
            .toolbar button.on{background:var(--themePrimary)} .toolbar .sp{flex:1}
            .frame{margin:16px auto;max-width:1280px;background:var(--white);box-shadow:0 2px 12px rgba(0,0,0,.15);transition:max-width .2s}
            .frame.mobile{max-width:400px}
            .suite{height:48px;background:var(--themeDarker);color:#fff;display:flex;align-items:center;gap:14px;padding:0 16px}
            .waffle{display:grid;grid-template-columns:repeat(3,4px);gap:3px} .waffle i{width:4px;height:4px;background:#fff;display:block}
            .search{flex:1;max-width:460px;margin:0 auto;background:rgba(255,255,255,.18);border-radius:4px;padding:6px 10px;color:#fff;opacity:.9}
            .shell{display:flex} .appbar{width:48px;background:var(--neutralLighter);display:flex;flex-direction:column;align-items:center;padding-top:10px;gap:14px}
            .appbar span{width:24px;height:24px;border-radius:5px;background:var(--neutralQuaternary)} .appbar span.home{background:var(--themePrimary)}
            .page{flex:1;min-width:0}
            .hubnav{display:flex;align-items:center;gap:4px;padding:0 20px;height:40px;background:var(--neutralLighterAlt);border-bottom:1px solid var(--neutralLight);position:relative}
            .hubnav .hubname{font-weight:600;margin-right:14px} .hubnav .item{padding:10px 10px;cursor:default;position:relative}
            .hubnav .item:hover{background:var(--neutralLight)}
            .mega{display:none;position:absolute;left:0;top:40px;background:var(--white);box-shadow:0 6px 18px rgba(0,0,0,.18);padding:18px 22px;min-width:520px;z-index:10;gap:28px}
            .item:hover .mega{display:flex} .mega h4{margin:0 0 6px;font-size:14px} .mega a{display:block;color:var(--neutralPrimary);text-decoration:none;padding:2px 0}
            .header{padding:22px 28px;display:flex;align-items:center;gap:16px;background:var(--white);border-bottom:1px solid var(--neutralLight)}
            .logo{width:56px;height:56px;border-radius:8px;background:var(--themePrimary);color:#fff;display:grid;place-items:center;font-weight:700;font-size:22px;flex:none}
            .header h1{margin:0;font-size:24px;font-weight:600} .header nav{display:flex;gap:18px;margin-top:6px;color:var(--neutralSecondary)}
            .logo.img{background:transparent;width:auto;max-width:192px;padding:0}.logo.img img{max-height:100%;max-width:192px;display:block}
            .layout-minimal .header{padding:8px 20px} .layout-minimal .logo{width:32px;height:32px;font-size:14px} .layout-minimal .header h1{font-size:18px} .layout-minimal .header nav{display:none}
            .layout-compact .header{padding:10px 20px} .layout-compact .logo{width:40px;height:40px;font-size:16px} .layout-compact .header .titles{display:flex;align-items:center;gap:28px} .layout-compact .header nav{margin:0}
            .layout-extended .header{background:linear-gradient(120deg,var(--themeDark),var(--themePrimary));color:#fff;padding:44px 32px;min-height:150px}
            .layout-extended .header nav{color:var(--themeLighter)} .layout-extended .logo{background:var(--white);color:var(--themePrimary)}
            .content{padding:20px 28px 36px;display:grid;gap:22px}
            .hero{display:grid;grid-template-columns:2fr 1fr 1fr;grid-template-rows:150px 150px;gap:6px}
            .tile{border-radius:2px;color:#fff;padding:14px;display:flex;align-items:flex-end;font-weight:600;font-size:16px}
            .tile:nth-child(1){grid-row:span 2;background:linear-gradient(160deg,var(--themeTertiary),var(--themeDarker));font-size:22px}
            .tile:nth-child(2){background:var(--themeSecondary)} .tile:nth-child(3){background:var(--themeDarkAlt)} .tile:nth-child(4){background:var(--themeDark)} .tile:nth-child(5){background:var(--themePrimary)}
            .cols{display:grid;grid-template-columns:2fr 1fr;gap:26px} h2{font-size:18px;margin:0 0 10px;font-weight:600}
            .news{display:flex;gap:12px;padding:10px 0;border-bottom:1px solid var(--neutralLight)} .thumb{width:120px;height:70px;background:var(--themeLighter);border-radius:2px;flex:none}
            .news .t{font-weight:600} .muted{color:var(--neutralSecondary);font-size:12px}
            .ql{display:grid;grid-template-columns:repeat(2,1fr);gap:8px} .ql div{border:1px solid var(--neutralLight);padding:10px;border-radius:2px;display:flex;gap:8px;align-items:center}
            .ql div::before{content:"";width:18px;height:18px;border-radius:4px;background:var(--themePrimary);flex:none}
            .events{display:grid;grid-template-columns:repeat(3,1fr);gap:10px} .ev{border:1px solid var(--neutralLight);padding:10px} .ev b{display:block;color:var(--themePrimary);font-size:20px}
            .btn{display:inline-block;background:var(--themePrimary);color:var(--white);padding:7px 16px;border-radius:2px;font-weight:600}
            a.link{color:var(--themePrimary)} a.link:hover{color:var(--themeDarkAlt)}
            footer{background:var(--neutralLighter);padding:18px 28px;display:flex;gap:22px;color:var(--neutralSecondary)}
            .frame.mobile .hero{grid-template-columns:1fr;grid-template-rows:none} .frame.mobile .tile{min-height:90px} .frame.mobile .tile:nth-child(1){grid-row:auto}
            .frame.mobile .cols,.frame.mobile .events{grid-template-columns:1fr} .frame.mobile .appbar,.frame.mobile .header nav,.frame.mobile .hubnav .item{display:none}
            .panel{display:none;max-width:1280px;margin:16px auto;background:#fff;color:#242424;padding:22px 28px;box-shadow:0 2px 12px rgba(0,0,0,.15)}
            .panel.on{display:block} .swatch{display:inline-flex;flex-direction:column;width:120px;margin:4px;font-size:11px;border:1px solid #ddd} .swatch i{height:38px;display:block}
            .tree ul{list-style:none;padding-left:18px;border-left:2px solid #ddd} .tree li{margin:4px 0} .tree .hub{font-weight:700}
            table{border-collapse:collapse} td,th{padding:4px 10px;border-bottom:1px solid #eee;text-align:left} .pass{color:#107c10;font-weight:700} .fail{color:#c50f1f;font-weight:700}
            </style></head><body>
            <div class="toolbar"><b>🏗️ Hubwright preview</b>
            <span>Header:</span>
            """);
        foreach (var layout in new[] { "minimal", "compact", "standard", "extended" })
        {
            sb.Append(CultureInfo.InvariantCulture, $"<button data-layout=\"{layout}\" class=\"{(layout == model.HeaderLayout ? "on" : "")}\">{layout}</button>");
        }

        sb.Append("""
            <span class="sp"></span><button id="mobile">📱 mobile</button><button data-tab="page" class="on">Page</button><button data-tab="ia">Structure</button><button data-tab="theme">Theme & contrast</button></div>
            """);

        sb.Append(CultureInfo.InvariantCulture, $"<div id=\"page\" class=\"frame layout-{E(model.HeaderLayout)}\"><div class=\"suite\"><div class=\"waffle\">{string.Concat(Enumerable.Repeat("<i></i>", 9))}</div><span>{E(model.Organization)}</span><div class=\"search\">🔍 Search</div></div>");
        sb.Append("<div class=\"shell\"><div class=\"appbar\"><span class=\"home\"></span><span></span><span></span><span></span></div><div class=\"page\">");

        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"hubnav\"><span class=\"hubname\">{E(model.HubTitle)}</span>");
        foreach (var node in model.HubNavigation.Take(8))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"item\">{E(node.Title)}{(node.Children.Count > 0 ? " ▾" : "")}");
            if (node.Children.Count > 0)
            {
                sb.Append("<div class=\"mega\">");
                var groups = node.Children.Any(c => c.Children.Count > 0) ? node.Children : [new NavNode { Title = node.Title, Children = node.Children }];
                foreach (var group in groups.Take(5))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<div><h4>{E(group.Title)}</h4>");
                    foreach (var link in group.Children.Take(10))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"<a>{E(link.Title)}{(link.Restricted ? " 🔒" : "")}</a>");
                    }

                    sb.Append("</div>");
                }

                sb.Append("</div>");
            }

            sb.Append("</div>");
        }

        var initials = string.Concat(model.Organization.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        sb.Append(CultureInfo.InvariantCulture, $"</div><div class=\"header\">{(string.IsNullOrWhiteSpace(model.Logo) ? $"<div class=\"logo\">{E(initials)}</div>" : $"<div class=\"logo img\"><img alt=\"logo\" src=\"{E(model.Logo)}\"></div>")}<div class=\"titles\"><h1>{E(model.SiteTitle)}</h1><nav><span>News</span><span>About us</span><span>Policies</span><span>Contacts</span></nav></div></div>");

        var tiles = new[] { $"Welcome to {model.SiteTitle}", "Quarterly results are in", "New starters: your first week", "Office moves this spring", "Security awareness month" };
        sb.Append("<div class=\"content\"><div class=\"hero\">");
        foreach (var tile in tiles)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"tile\">{E(tile)}</div>");
        }

        sb.Append("</div><div class=\"cols\"><div><h2>News</h2>");
        foreach (var (title, meta) in new[] { ("How we're using Copilot safely", "Communications · 2 days ago"), ("Benefits enrolment opens Monday", "HR · 4 days ago"), ("Meet the new service desk", "IT · last week") })
        {
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"news\"><div class=\"thumb\"></div><div><div class=\"t\">{E(title)}</div><div class=\"muted\">{E(meta)}</div><a class=\"link\">Read more</a></div></div>");
        }

        sb.Append("</div><div><h2>Quick links</h2><div class=\"ql\">");
        foreach (var link in (model.QuickLinks.Count > 0 ? model.QuickLinks : ["HR", "IT help", "Expenses", "Policies"]).Take(8))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<div>{E(link)}</div>");
        }

        sb.Append("</div><p><span class=\"btn\">Ask HR</span></p></div></div><div><h2>Events</h2><div class=\"events\">");
        foreach (var (day, title) in new[] { ("12", "Town hall"), ("18", "Lunch & learn: Teams tips"), ("25", "Charity run") })
        {
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"ev\"><b>{day}</b>{E(title)}<div class=\"muted\">10:00–11:00</div></div>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"</div></div></div><footer><b>{E(model.Organization)}</b><span>Contact</span><span>Accessibility</span><span>Privacy</span></footer></div></div></div>");

        // Structure tab: the hub families.
        sb.Append("<div id=\"ia\" class=\"panel\"><h2>Structure</h2><div class=\"tree\"><ul>");
        sb.Append(CultureInfo.InvariantCulture, $"<li class=\"hub\">🏠 {E(model.SiteTitle)} (home site)</li>");
        foreach (var hub in model.Hubs)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<li><span class=\"hub\">🧭 {E(hub.Title)}</span>{(hub.Parent is null ? "" : $" <span class=\"muted\">→ {E(hub.Parent)}</span>")}<ul>");
            foreach (var site in hub.Sites)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<li>📄 {E(site)}</li>");
            }

            sb.Append("</ul></li>");
        }

        sb.Append("</ul></div>");
        if (model.Notes.Count > 0)
        {
            sb.Append("<h3>Why this shape</h3><ul>");
            foreach (var note in model.Notes)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<li>{E(note)}</li>");
            }

            sb.Append("</ul>");
        }

        sb.Append("</div>");

        // Theme tab: palette and contrast checks.
        sb.Append(CultureInfo.InvariantCulture, $"<div id=\"theme\" class=\"panel\"><h2>{E(model.Theme.Name)}</h2><p>{(model.Theme.Accessible ? "✅ Every text pair passes WCAG 2.1 AA." : "⚠️ Some text pairs fail WCAG 2.1 AA; see below.")}</p>");
        foreach (var (slot, value) in p)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<span class=\"swatch\"><i style=\"background:{value}\"></i>{E(slot)}<br>{value}</span>");
        }

        sb.Append("<h3>Contrast</h3><table><tr><th>Where</th><th>Sample</th><th>Ratio</th><th>Needs</th><th></th></tr>");
        foreach (var check in model.Theme.Checks)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tr><td>{E(check.Usage)}</td><td><span style=\"background:{check.Background};color:{check.Foreground};padding:3px 10px;border:1px solid #ddd\">Aa sample</span></td><td>{check.Ratio:0.00}:1</td><td>{check.Required:0.0}:1</td><td class=\"{(check.Passes ? "pass" : "fail")}\">{(check.Passes ? "pass" : "fail")}</td></tr>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"</table><h3>Theme JSON</h3><pre>{E(model.Theme.ToJson())}</pre></div>");
        sb.Append(CultureInfo.InvariantCulture, $$"""
            <script>
            const theme = {{theme}};
            const page = document.getElementById('page');
            document.querySelectorAll('[data-layout]').forEach(b => b.onclick = () => {
              page.className = page.className.replace(/layout-\w+/, 'layout-' + b.dataset.layout);
              document.querySelectorAll('[data-layout]').forEach(x => x.classList.toggle('on', x === b));
            });
            document.getElementById('mobile').onclick = e => { page.classList.toggle('mobile'); e.target.classList.toggle('on'); };
            document.querySelectorAll('[data-tab]').forEach(b => b.onclick = () => {
              document.querySelectorAll('[data-tab]').forEach(x => x.classList.toggle('on', x === b));
              page.style.display = b.dataset.tab === 'page' ? '' : 'none';
              ['ia', 'theme'].forEach(id => document.getElementById(id).classList.toggle('on', b.dataset.tab === id));
            });
            </script></body></html>
            """);
        return sb.ToString();
    }
}
