using System.Net;
using System.Text;
using Hubwright.Theming;

namespace Hubwright.Templates;

/// <summary>
/// Wireframe sketches of a template's home page in a theme, and a stand-alone gallery of the
/// catalogue. A sketch is Hubwright's approximation from the template's sections, not Microsoft's
/// layout; the gallery says so.
/// </summary>
public static class TemplateGallery
{
    public const string Disclaimer = "Sketches are Hubwright's wireframes of each template's home page, drawn in your theme. Apply the template in SharePoint to see Microsoft's exact pages.";

    /// <summary>A small wireframe of one template, as an HTML fragment that uses the gallery's CSS.</summary>
    public static string Sketch(SiteTemplate template, SharePointTheme theme)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(theme);
        var p = theme.Palette;
        var sb = new StringBuilder();
        sb.Append(CultureInvariant($"<div class=\"sk\" style=\"--p:{p.GetValueOrDefault("themePrimary", "#0f6cbd")};--l:{p.GetValueOrDefault("themeLighter", "#deecf9")};--d:{p.GetValueOrDefault("themeDarker", "#004578")}\">"));
        sb.Append("<div class=\"sk-bar\"><i></i><b></b></div>");
        foreach (var section in template.Sections.Count > 0 ? template.Sections : ["hero", "news", "quicklinks"])
        {
            sb.Append(section.ToLowerInvariant() switch
            {
                "hero" => "<div class=\"sk-hero\"><span></span><span></span><span></span></div>",
                "news" => "<div class=\"sk-row\"><em>News</em><div class=\"sk-cards\"><i></i><i></i><i></i></div></div>",
                "events" or "calendar" => "<div class=\"sk-row\"><em>Events</em><div class=\"sk-list\"><i></i><i></i></div></div>",
                "quicklinks" => "<div class=\"sk-row\"><em>Quick links</em><div class=\"sk-dots\"><i></i><i></i><i></i><i></i><i></i><i></i></div></div>",
                "documents" => "<div class=\"sk-row\"><em>Documents</em><div class=\"sk-list\"><i></i><i></i><i></i></div></div>",
                "people" => "<div class=\"sk-row\"><em>People</em><div class=\"sk-dots round\"><i></i><i></i><i></i><i></i></div></div>",
                "gallery" or "video" => "<div class=\"sk-row\"><em>" + (section == "video" ? "Video" : "Gallery") + "</em><div class=\"sk-cards big\"><i></i><i></i></div></div>",
                "faq" => "<div class=\"sk-row\"><em>FAQ</em><div class=\"sk-list\"><i></i><i></i></div></div>",
                "conversations" => "<div class=\"sk-row\"><em>Conversations</em><div class=\"sk-list\"><i></i><i></i></div></div>",
                "tasks" => "<div class=\"sk-row\"><em>Tasks</em><div class=\"sk-list\"><i></i><i></i></div></div>",
                "text" => "<div class=\"sk-row\"><em>About</em><div class=\"sk-text\"><i></i><i></i></div></div>",
                _ => $"<div class=\"sk-row\"><em>{WebUtility.HtmlEncode(section)}</em><div class=\"sk-text\"><i></i></div></div>",
            });
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    public const string Css = """
        .sk{border:1px solid #e1dfdd;border-radius:8px;overflow:hidden;background:#fff;font:10px Segoe UI,system-ui,sans-serif;color:#605e5c}
        .sk-bar{height:22px;background:var(--d);display:flex;align-items:center;gap:6px;padding:0 8px}.sk-bar i{width:12px;height:12px;border-radius:3px;background:var(--p);display:block}.sk-bar b{width:60px;height:6px;border-radius:3px;background:#ffffff88;display:block}
        .sk-hero{display:grid;grid-template-columns:2fr 1fr 1fr;gap:3px;padding:6px}.sk-hero span{height:44px;background:var(--l);border-radius:3px;display:block}.sk-hero span:first-child{background:var(--p);opacity:.85}
        .sk-row{padding:4px 8px}.sk-row em{font-style:normal;font-weight:600;display:block;margin-bottom:3px;color:#323130}
        .sk-cards{display:grid;grid-template-columns:repeat(3,1fr);gap:4px}.sk-cards.big{grid-template-columns:repeat(2,1fr)}.sk-cards i{height:22px;background:var(--l);border-radius:3px;display:block}.sk-cards.big i{height:34px}
        .sk-list i{height:6px;background:#edebe9;border-radius:3px;display:block;margin:3px 0}.sk-list i:first-child{width:80%}
        .sk-dots{display:flex;gap:5px}.sk-dots i{width:14px;height:14px;border-radius:3px;background:var(--p);opacity:.7;display:block}.sk-dots.round i{border-radius:50%}
        .sk-text i{height:5px;background:#edebe9;border-radius:3px;display:block;margin:3px 0}
        """;

    /// <summary>The whole catalogue as one page: Microsoft's templates, then look book packages.</summary>
    public static string Render(IEnumerable<SiteTemplate> catalog, SharePointTheme theme, string? highlight = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var list = catalog.ToList();
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>SharePoint site templates</title><style>");
        sb.Append("body{font:14px Segoe UI,system-ui,sans-serif;margin:0;padding:24px 28px;background:#faf9f8;color:#323130}h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:26px 0 10px}p.note{color:#605e5c;margin:0 0 8px;max-width:900px}");
        sb.Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:14px}.card{background:#fff;border:1px solid #edebe9;border-radius:10px;padding:12px;display:flex;flex-direction:column;gap:8px}.card.hi{outline:3px solid #6b4fd8}");
        sb.Append(".card h3{font-size:14px;margin:0}.card p{margin:0;font-size:12px;color:#605e5c}.tag{display:inline-block;font-size:10px;font-weight:600;padding:1px 7px;border-radius:9px;background:#efeafc;color:#4b32a8;margin-right:4px}.how{font:11px Cascadia Mono,Consolas,monospace;color:#3b3a39;background:#f3f2f1;border-radius:6px;padding:6px;word-break:break-all}a{color:#4b32a8}");
        sb.Append(Css).Append("</style></head><body>");
        sb.Append("<h1>SharePoint site templates</h1><p class=\"note\">").Append(WebUtility.HtmlEncode(Disclaimer)).Append("</p>");
        foreach (var group in new[] { (TemplateSource.Microsoft, "Built into SharePoint (Settings → Apply a site template)"), (TemplateSource.PnpPackage, "Look book packages (PnP provisioning templates)") })
        {
            var items = list.Where(t => t.Source == group.Item1).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            sb.Append("<h2>").Append(WebUtility.HtmlEncode(group.Item2)).Append(CultureInvariant($" · {items.Count}</h2><div class=\"grid\">"));
            foreach (var t in items)
            {
                sb.Append("<div class=\"card").Append(string.Equals(t.Id, highlight, StringComparison.OrdinalIgnoreCase) ? " hi" : "").Append("\" id=\"").Append(WebUtility.HtmlEncode(t.Id)).Append("\">");
                sb.Append(Sketch(t, theme));
                sb.Append("<h3>").Append(WebUtility.HtmlEncode(t.Name)).Append("</h3><div>");
                sb.Append("<span class=\"tag\">").Append(t.Kind == Snapshot.SiteKind.Communication ? "communication" : "team").Append("</span>");
                if (t.Category is not null)
                {
                    sb.Append("<span class=\"tag\">").Append(WebUtility.HtmlEncode(t.Category)).Append("</span>");
                }

                foreach (var pre in t.Prerequisites)
                {
                    sb.Append("<span class=\"tag\">").Append(WebUtility.HtmlEncode(pre)).Append("</span>");
                }

                sb.Append("</div><p>").Append(WebUtility.HtmlEncode(t.Description)).Append("</p>");
                sb.Append("<div class=\"how\">").Append(WebUtility.HtmlEncode(t.HowToApply)).Append("</div>");
                sb.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(t.Package ?? t.SourceUrl)).Append("\" target=\"_blank\">").Append(t.Package is null ? "Microsoft's description" : "Download the .pnp package").Append("</a></p></div>");
            }

            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);
}
