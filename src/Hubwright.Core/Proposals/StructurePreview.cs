using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hubwright.Hints;
using Hubwright.Snapshot;

namespace Hubwright.Proposals;

/// <summary>
/// An interactive picture of a proposal's structure: the target (home site, hubs and their sites),
/// today next to the target, and the list of moves. Selecting a site shows where it is today, where
/// it goes, what happens to it and why. With an implementation's statuses it shows progress too.
/// One self-contained HTML page: no network, no scripts from elsewhere.
/// </summary>
public static class StructurePreview
{
    public static string Render(IntranetProposal proposal, TenantSnapshot? today = null, HintBook? hints = null, IReadOnlyDictionary<string, string>? statusByUrl = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        hints ??= HintBook.Empty;
        var b = proposal.Blueprint;
        var palette = b.Theme().Palette;
        string? Status(string? url) => url is not null && statusByUrl is not null && statusByUrl.TryGetValue(url.TrimEnd('/'), out var s) ? s : null;
        var newByUrl = proposal.NewSites.GroupBy(n => n.Url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var planByUrl = proposal.Sites.GroupBy(s => s.Url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        JsonObject Site(string title, string url, string kind, bool existing, string? purpose = null)
        {
            var key = url.TrimEnd('/');
            var node = new JsonObject
            {
                ["title"] = title,
                ["url"] = url,
                ["kind"] = kind,
                ["existing"] = existing,
                ["purpose"] = purpose,
                ["status"] = Status(url),
            };
            if (newByUrl.TryGetValue(key, out var n))
            {
                node["template"] = n.TemplateName;
                node["how"] = n.HowToApply;
            }

            if (planByUrl.TryGetValue(key, out var plan))
            {
                node["disposition"] = plan.Disposition;
            }
            else if (!existing)
            {
                node["disposition"] = "new";
            }

            return node;
        }

        var target = new JsonObject
        {
            ["home"] = Site(b.Home.Title, b.Home.ExistingUrl ?? b.Url(b.Home.Slug), "communication", b.Home.ExistingUrl is not null, b.Home.Purpose),
            ["hubs"] = new JsonArray([.. b.Hubs.Select(h =>
            {
                var hub = Site(h.Title, h.ExistingUrl ?? b.Url(h.Slug), "hub", h.ExistingUrl is not null, h.Purpose);
                var sites = new JsonArray([.. h.Sites.Select(s => (JsonNode)Site(s.Title, s.ExistingUrl ?? b.Url(s.Slug), s.Kind == SiteKind.Communication ? "communication" : "team", s.ExistingUrl is not null, s.Purpose))]);
                foreach (var moved in proposal.Sites.Where(p => p.TargetHub == h.Title && p.Url.TrimEnd('/') != (h.ExistingUrl ?? "").TrimEnd('/') && h.Sites.All(s => !string.Equals(s.ExistingUrl, p.Url, StringComparison.OrdinalIgnoreCase))))
                {
                    sites.Add(Site(moved.Title, moved.Url, moved.Kind == SiteKind.Communication ? "communication" : "team", existing: true));
                }

                hub["sites"] = sites;
                return (JsonNode)hub;
            })]),
            ["standalone"] = new JsonArray([.. b.Standalone.Select(s => (JsonNode)Site(s.Title, s.ExistingUrl ?? b.Url(s.Slug), s.Kind == SiteKind.Communication ? "communication" : "team", s.ExistingUrl is not null, s.Purpose))]),
            ["outside"] = new JsonArray([.. proposal.Sites.Where(p => p.TargetHub is null && p.Disposition is "keep" or "review" && !p.IsHub && p.Url != b.Home.ExistingUrl).Take(80).Select(p => (JsonNode)Site(p.Title, p.Url, p.Kind == SiteKind.Communication ? "communication" : "team", existing: true))]),
        };

        var now = new JsonObject();
        if (today is not null)
        {
            var hubTitles = today.Hubs.GroupBy(h => h.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            JsonObject Today(SiteInfo s) => new()
            {
                ["title"] = s.Title,
                ["url"] = s.Url,
                ["kind"] = s.IsHub ? "hub" : s.Kind == SiteKind.Communication ? "communication" : s.Kind == SiteKind.Classic ? "classic" : "team",
                ["disposition"] = planByUrl.TryGetValue(s.Url.TrimEnd('/'), out var p) ? p.Disposition : null,
            };
            now["hubs"] = new JsonArray([.. today.Hubs.Select(h => (JsonNode)new JsonObject
            {
                ["title"] = h.Title,
                ["url"] = h.SiteUrl,
                ["parent"] = h.ParentHubId is { } parent && hubTitles.TryGetValue(parent, out var ph) ? ph.Title : null,
                ["disposition"] = h.SiteUrl.Length > 0 && planByUrl.TryGetValue(h.SiteUrl.TrimEnd('/'), out var hp) ? hp.Disposition : null,
                ["sites"] = new JsonArray([.. today.Sites.Where(s => s.HubId == h.Id && !s.IsHub).Select(s => (JsonNode)Today(s))]),
            })]);
            now["orphans"] = new JsonArray([.. today.Sites.Where(s => s.HubId is null && !s.IsHub).Select(s => (JsonNode)Today(s))]);
        }

        var plans = new JsonObject();
        foreach (var p in proposal.Sites)
        {
            plans[p.Url.TrimEnd('/')] = new JsonObject
            {
                ["title"] = p.Title,
                ["today"] = p.CurrentHub ?? "no hub",
                ["target"] = p.TargetHub,
                ["disposition"] = p.Disposition,
                ["reasons"] = new JsonArray([.. p.Reasons.Select(r => (JsonNode)r)]),
                ["actions"] = new JsonArray([.. p.Actions.Distinct().Select(a =>
                {
                    var hint = hints.For(a);
                    return (JsonNode)new JsonObject { ["name"] = Words(a.ToString()), ["what"] = hint?.What, ["why"] = hint?.Why };
                })]),
            };
        }

        var data = new JsonObject { ["target"] = target, ["today"] = now, ["plans"] = plans, ["organization"] = b.Organization };
        var json = data.ToJsonString(new JsonSerializerOptions { WriteIndented = false }).Replace("</", "<\\/", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(proposal.Name)).Append(" · structure</title><style>");
        sb.Append(FormattableString.Invariant($":root{{--p:{palette.GetValueOrDefault("themePrimary")};--d:{palette.GetValueOrDefault("themeDarker")};--l:{palette.GetValueOrDefault("themeLighter")};--la:{palette.GetValueOrDefault("themeLighterAlt")}}}"));
        sb.Append(Css);
        sb.Append("</style></head><body><div class=\"bar\"><b>Structure</b><span class=\"tabs\"><button data-v=\"target\" class=\"on\">Target</button><button data-v=\"compare\">Today → Target</button><button data-v=\"moves\">Moves</button></span>");
        sb.Append("<span class=\"legend\"><i class=\"d-new\"></i>new <i class=\"d-keep\"></i>kept <i class=\"d-move\"></i>moved <i class=\"d-modernize\"></i>modernize <i class=\"d-review\"></i>review <i class=\"d-archive\"></i>archive / retire</span></div>");
        sb.Append("<div id=\"view\"></div><aside id=\"detail\" class=\"hide\"></aside><script>const D=").Append(json).Append(';').Append(Script).Append("</script></body></html>");
        return sb.ToString();
    }

    private static string Words(string pascal) => string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    private const string Css = """
        *{box-sizing:border-box}body{margin:0;font:13px/1.45 Segoe UI,system-ui,sans-serif;color:#242424;background:#faf9f8}
        .bar{position:sticky;top:0;z-index:3;display:flex;gap:14px;align-items:center;flex-wrap:wrap;padding:8px 14px;background:#fff;border-bottom:1px solid #edebe9}
        .tabs button{border:1px solid #e1dfdd;background:#fff;border-radius:14px;padding:3px 12px;cursor:pointer;margin-right:4px}.tabs button.on{background:var(--p);border-color:var(--p);color:#fff}
        .legend{color:#605e5c;font-size:11px}.legend i{display:inline-block;width:10px;height:10px;border-radius:3px;margin:0 3px 0 8px;vertical-align:-1px}
        #view{padding:14px}.home{max-width:420px;margin:0 auto 14px}.cols{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:12px;align-items:start}
        .hub{background:#fff;border:1px solid #edebe9;border-radius:10px;overflow:hidden}.hub>.head{background:var(--la);border-bottom:3px solid var(--p);padding:8px 10px}
        .hub.today>.head{border-bottom-color:#8a8886;background:#f3f2f1}.hub .sites{padding:6px}
        .node{display:flex;gap:6px;align-items:flex-start;padding:5px 7px;border-radius:7px;cursor:pointer;border:1px solid transparent;border-left:4px solid #c8c6c4;margin:3px 0;background:#fff}
        .node:hover{border-color:#c8c6c4}.node.sel{outline:2px solid var(--p);outline-offset:1px}.node .t{flex:1}.node small{display:block;color:#605e5c;font-size:11px}
        .head .node{background:transparent;border-left-width:4px;margin:0}.badge{font-size:10px;font-weight:600;border-radius:8px;padding:0 6px;white-space:nowrap}
        .d-new{background:#107c10}.d-keep{background:#8a8886}.d-move{background:#0f6cbd}.d-modernize{background:#c97a00}.d-review{background:#605e5c}.d-archive,.d-retire{background:#d13438}
        .node.d-new{border-left-color:#107c10}.node.d-keep{border-left-color:#8a8886}.node.d-move{border-left-color:#0f6cbd}.node.d-modernize{border-left-color:#c97a00}.node.d-review{border-left-color:#605e5c}.node.d-archive,.node.d-retire{border-left-color:#d13438;opacity:.75}
        .st{font-size:10px;font-weight:600;border-radius:8px;padding:0 6px;background:#f3f2f1;color:#605e5c}.st.done{background:#dff6dd;color:#107c10}.st.failed{background:#fde7e9;color:#a4262c}.st.running,.st.blocked{background:#fff4ce;color:#8a5d00}
        .compare{display:grid;grid-template-columns:1fr 1fr;gap:16px}.compare h3{margin:0 0 8px;font-size:14px}.arrow{color:#605e5c;font-size:11px}
        table{border-collapse:collapse;width:100%;background:#fff}td,th{border-bottom:1px solid #edebe9;padding:6px 8px;text-align:left;vertical-align:top}th{background:#f3f2f1}
        aside{position:fixed;right:12px;top:52px;bottom:12px;width:340px;background:#fff;border:1px solid #e1dfdd;border-radius:12px;box-shadow:0 8px 24px #0002;padding:14px;overflow:auto;z-index:4}aside.hide{display:none}
        aside h4{margin:0 0 4px;font-size:15px}aside .x{float:right;cursor:pointer;border:0;background:none;font-size:16px}aside .act{background:#faf9f8;border-radius:8px;padding:6px 8px;margin:6px 0}aside .act b{display:block}
        button.open{border:0;background:var(--p);color:#fff;border-radius:6px;padding:6px 12px;cursor:pointer}
        .muted{color:#605e5c}.empty{color:#605e5c;font-style:italic;padding:6px}
        """;

    private const string Script = """
        const P=D.plans||{};const esc=s=>String(s??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
        const key=u=>String(u||'').replace(/\/$/,'').toLowerCase();
        function disp(n){return n.disposition||(n.existing?'keep':'new')}
        function node(n,extra){const d=disp(n);const st=n.status?`<span class="st ${esc(n.status.toLowerCase())}">${esc(n.status)}</span>`:'';
          const icon=n.kind==='hub'?'🧭':n.kind==='team'?'👥':n.kind==='classic'?'🏚':'📄';
          return `<div class="node d-${esc(d)}" data-url="${esc(key(n.url))}" title="${esc(n.url)}">${icon}<span class="t">${esc(n.title)}<small>${extra||(n.template?esc(n.template):esc(d))}</small></span>${st}</div>`}
        function hubCol(h,cls){const sites=(h.sites||[]);return `<div class="hub ${cls||''}"><div class="head">${node(h,h.parent?('under '+esc(h.parent)):(h.existing===false?'new hub':'hub'))}</div><div class="sites">${sites.length?sites.map(s=>node(s)).join(''):'<div class="empty">no sites</div>'}</div></div>`}
        function target(){const t=D.target;let h=`<div class="home">${node(t.home,'home site · '+(t.home.existing?'kept':'new'))}</div><div class="cols">`;
          h+=t.hubs.map(x=>hubCol(x)).join('');
          if(t.standalone.length)h+=hubCol({title:'Outside the hubs',kind:'hub',existing:true,sites:t.standalone});
          if(t.outside.length)h+=hubCol({title:'Team spaces and sites without a hub ('+t.outside.length+')',kind:'hub',existing:true,sites:t.outside});
          return h+'</div>'}
        function today(){const n=D.today;if(!n.hubs)return '<div class="empty">No scan to compare with.</div>';
          let h='<div class="cols">'+n.hubs.map(x=>hubCol(x,'today')).join('');
          if(n.orphans.length)h+=hubCol({title:'In no hub ('+n.orphans.length+')',kind:'hub',sites:n.orphans},'today');
          return h+'</div>'}
        function moves(){const rows=Object.entries(P).filter(([u,p])=>p.disposition!=='keep').sort((a,b)=>a[1].disposition.localeCompare(b[1].disposition));
          if(!rows.length)return '<div class="empty">Nothing moves.</div>';
          return '<table><tr><th>Site</th><th>Today</th><th></th><th>Target</th><th>Plan</th><th>Why</th></tr>'+rows.map(([u,p])=>`<tr class="node-row" data-url="${esc(u)}"><td>${esc(p.title)}</td><td>${esc(p.today)}</td><td class="arrow">→</td><td>${esc(p.target||'–')}</td><td><span class="badge d-${esc(p.disposition)}" style="color:#fff">${esc(p.disposition)}</span></td><td>${esc((p.reasons||[]).join(' '))}</td></tr>`).join('')+'</table>'}
        function show(v){document.querySelectorAll('.tabs button').forEach(b=>b.classList.toggle('on',b.dataset.v===v));
          const view=document.getElementById('view');
          view.innerHTML=v==='target'?target():v==='compare'?`<div class="compare"><div><h3>Today</h3>${today()}</div><div><h3>Target</h3>${target()}</div></div>`:moves();
          view.querySelectorAll('[data-url]').forEach(e=>e.onclick=()=>select(e.dataset.url))}
        function find(u){const all=[D.target.home,...D.target.hubs,...D.target.hubs.flatMap(h=>h.sites),...D.target.standalone,...D.target.outside];return all.find(n=>key(n.url)===u)}
        function select(u){document.querySelectorAll('.node').forEach(e=>e.classList.toggle('sel',e.dataset.url===u));
          const p=P[u];const n=find(u);const a=document.getElementById('detail');a.classList.remove('hide');
          let h=`<button class="x" onclick="this.parentNode.classList.add('hide')">✕</button><h4>${esc((p||n||{}).title)}</h4><div class="muted">${esc(n?n.url:u)}</div>`;
          if(p){h+=`<p><b>${esc(p.today)}</b> <span class="arrow">→</span> <b>${esc(p.target||'outside the hubs')}</b> · <span class="badge d-${esc(p.disposition)}" style="color:#fff">${esc(p.disposition)}</span></p>`;
            h+=(p.reasons||[]).map(r=>`<p>${esc(r)}</p>`).join('');
            h+=(p.actions||[]).filter(x=>x.what).map(x=>`<div class="act"><b>${esc(x.name)}</b>${esc(x.what)}<div class="muted">Why: ${esc(x.why)}</div></div>`).join('')}
          else if(n){h+=`<p>${n.existing?'Kept as it is.':'New site'}${n.template?' · template: <b>'+esc(n.template)+'</b>':''}</p>${n.purpose?'<p>'+esc(n.purpose)+'</p>':''}${n.how?'<p class="muted">'+esc(n.how)+'</p>':''}`}
          if(n&&n.status)h+=`<p>Implementation: <span class="st ${esc(n.status.toLowerCase())}">${esc(n.status)}</span></p>`;
          if(n||p)h+=`<p><button class="open" data-open="${esc(n?n.url:u)}">Open in site preview →</button></p>`;
          a.innerHTML=h;a.querySelectorAll('[data-open]').forEach(b=>b.onclick=()=>openPreview(b.dataset.open))}
        function openPreview(url){const msg={hubwright:'open',url};if(window.parent!==window)window.parent.postMessage(msg,'*');else if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(msg)}
        document.querySelectorAll('.tabs button').forEach(b=>b.onclick=()=>show(b.dataset.v));show('target');
        """;
}
