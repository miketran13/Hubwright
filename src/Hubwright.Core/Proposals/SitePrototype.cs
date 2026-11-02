using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hubwright.Snapshot;
using Hubwright.Templates;

namespace Hubwright.Proposals;

/// <summary>
/// A clickable prototype of the proposed intranet: every site (home, hubs, their sites, kept and new)
/// as a page in the proposal's theme and logo, with the global navigation, each hub's menu,
/// breadcrumbs, and sample content drawn from each site's template. Back and forward move between
/// pages. Everything is sample content, labelled as such; nothing comes from or goes to the tenant.
/// One self-contained HTML page.
/// </summary>
public static class SitePrototype
{
    /// <summary>The key a site is addressed by in the prototype (and in <c>#site=</c> links).</summary>
    public static string Key(string url) => url.TrimEnd('/').ToLowerInvariant();

    public static string Render(IntranetProposal proposal, IReadOnlyList<SiteTemplate> catalog, string? logo = null, IReadOnlyDictionary<string, string>? statusByUrl = null, string? start = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(catalog);
        var b = proposal.Blueprint;
        var theme = b.Theme();
        var templates = catalog.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var newByUrl = proposal.NewSites.GroupBy(n => Key(n.Url), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var designs = proposal.Designs.GroupBy(d => Key(d.Url)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var sites = new JsonArray();
        var aliases = new JsonObject();

        JsonArray Nav(IEnumerable<NavNode> nodes) => new([.. nodes.Select(n => (JsonNode)new JsonObject
        {
            ["title"] = n.Title,
            ["url"] = n.Url,
            ["audience"] = n.AudienceTargeted,
            ["children"] = Nav(n.Children),
        })]);

        void Add(string title, string url, string kind, string? hub, string? purpose, string? templateId, bool existing, string? alias = null, JsonArray? nav = null)
        {
            var key = Key(url);
            if (sites.Any(s => s!["key"]!.GetValue<string>() == key))
            {
                return;
            }

            templateId ??= newByUrl.TryGetValue(key, out var n) ? n.TemplateId : null;
            var template = templateId is not null && templates.TryGetValue(templateId, out var t) ? t : null;
            var design = designs.GetValueOrDefault(key);
            var sections = design?.Sections is { Count: > 0 } ds ? ds : template?.Sections is { Count: > 0 } s ? s : kind switch
            {
                "home" => ["hero", "news", "quicklinks", "events"],
                "hub" => ["hero", "news", "quicklinks", "documents"],
                "team" => ["news", "documents", "conversations", "quicklinks"],
                _ => ["hero", "news", "quicklinks", "documents"],
            };
            sites.Add(new JsonObject
            {
                ["key"] = key,
                ["url"] = url,
                ["title"] = title,
                ["kind"] = kind,
                ["hub"] = hub,
                ["purpose"] = design?.Purpose ?? purpose,
                ["design"] = design?.PatternName,
                ["links"] = new JsonArray([.. (design?.Navigation ?? []).Select(x => (JsonNode)x)]),
                ["template"] = template?.Name,
                ["sections"] = new JsonArray([.. sections.Select(x => (JsonNode)x)]),
                ["existing"] = existing,
                ["status"] = statusByUrl is not null && statusByUrl.TryGetValue(url.TrimEnd('/'), out var st) ? st : null,
                ["nav"] = nav,
            });
            if (alias is not null && Key(alias) != key)
            {
                aliases[Key(alias)] = key;
            }
        }

        var homeUrl = b.Home.ExistingUrl ?? b.Url(b.Home.Slug);
        Add(b.Home.Title, homeUrl, "home", null, b.Home.Purpose, b.Home.Template, b.Home.ExistingUrl is not null, b.Url(b.Home.Slug));
        foreach (var hub in b.Hubs)
        {
            var hubUrl = hub.ExistingUrl ?? b.Url(hub.Slug);
            Add(hub.Title, hubUrl, "hub", hub.Title, hub.Purpose, null, hub.ExistingUrl is not null, b.Url(hub.Slug), Nav(hub.Navigation));
            foreach (var site in hub.Sites)
            {
                Add(site.Title, site.ExistingUrl ?? b.Url(site.Slug), site.Kind == SiteKind.Communication ? "communication" : "team", hub.Title, site.Purpose, site.Template, site.ExistingUrl is not null, b.Url(site.Slug));
            }

            foreach (var moved in proposal.Sites.Where(p => p.TargetHub == hub.Title && Key(p.Url) != Key(hubUrl)))
            {
                Add(moved.Title, moved.Url, moved.Kind == SiteKind.Communication ? "communication" : "team", hub.Title, string.Join(" ", moved.Reasons.Take(1)), null, existing: true);
            }
        }

        foreach (var site in b.Standalone)
        {
            Add(site.Title, site.ExistingUrl ?? b.Url(site.Slug), site.Kind == SiteKind.Communication ? "communication" : "team", null, site.Purpose, site.Template, site.ExistingUrl is not null, b.Url(site.Slug));
        }

        // Every other designed site (kept where it is today), so its design can be previewed too.
        foreach (var d in proposal.Designs)
        {
            Add(d.Title, d.Url, d.Kind == SiteKind.Communication ? "communication" : "team", null, d.Purpose, d.TemplateId, !d.IsNew);
        }

        var data = new JsonObject
        {
            ["organization"] = b.Organization,
            ["home"] = Key(homeUrl),
            ["start"] = start is null ? null : Key(start),
            ["layout"] = b.HeaderLayout,
            ["logo"] = logo,
            ["global"] = Nav(b.GlobalNavigation),
            ["sites"] = sites,
            ["aliases"] = aliases,
            ["palette"] = new JsonObject([.. theme.Palette.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))]),
        };
        var json = data.ToJsonString().Replace("</", "<\\/", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(WebUtility.HtmlEncode(b.Organization)).Append(" intranet · preview</title><style>").Append(Css).Append("</style></head><body>")
            .Append("<div class=\"tool\"><button id=\"back\" title=\"Back\">←</button><button id=\"fwd\" title=\"Forward\">→</button><button id=\"homeBtn\" title=\"Home site\">⌂</button>")
            .Append("<select id=\"jump\" title=\"Go to a site\"></select><span class=\"sp\"></span><button id=\"mobile\" title=\"Phone width\">📱</button>")
            .Append("<span class=\"sample\">Preview with sample content</span></div><div id=\"frame\"><div id=\"page\"></div></div><script>const D=")
            .Append(json).Append(';').Append(Script).Append("</script></body></html>");
        return sb.ToString();
    }

    private const string Css = """
        *{box-sizing:border-box}body{margin:0;font:14px/1.45 Segoe UI,system-ui,sans-serif;color:var(--neutralPrimary,#323130);background:#e9e7e4}
        .tool{position:sticky;top:0;z-index:20;display:flex;gap:6px;align-items:center;padding:6px 10px;background:#fff;border-bottom:1px solid #e1dfdd}
        .tool button{border:1px solid #e1dfdd;background:#fff;border-radius:6px;min-width:32px;height:28px;cursor:pointer}.tool button:disabled{opacity:.4;cursor:default}
        .tool select{height:28px;border:1px solid #e1dfdd;border-radius:6px;max-width:340px}.sp{flex:1}.sample{font-size:11px;font-weight:600;color:#8a5d00;background:#fff4ce;border-radius:10px;padding:2px 9px}
        #frame{margin:0 auto;max-width:1280px;background:var(--white,#fff);min-height:calc(100vh - 41px);box-shadow:0 0 0 1px #e1dfdd}#frame.phone{max-width:390px}
        .suite{height:44px;background:var(--themeDarker);color:#fff;display:flex;align-items:center;gap:12px;padding:0 14px;font-weight:600}.suite .waffle{opacity:.85}.suite .search{flex:1;max-width:420px;margin:0 auto;height:30px;border-radius:6px;background:#ffffff26;display:flex;align-items:center;padding:0 10px;font-weight:400;font-size:12px;color:#ffffffcc}
        .body{display:flex}.appbar{width:48px;background:var(--neutralLighter,#f3f2f1);display:flex;flex-direction:column;align-items:center;gap:14px;padding-top:12px;position:relative}
        .appbar button{width:32px;height:32px;border:0;border-radius:6px;background:transparent;cursor:pointer;font-size:16px}.appbar button.on{background:var(--themeLighter)}
        .flyout{position:absolute;left:48px;top:0;width:280px;background:#fff;box-shadow:4px 0 16px #0002;padding:14px;z-index:10;display:none;max-height:80vh;overflow:auto}.flyout.open{display:block}.flyout h4{margin:0 0 8px}
        .flyout a,.mega a{display:block;padding:4px 6px;border-radius:4px;color:inherit;text-decoration:none;cursor:pointer}.flyout a:hover,.mega a:hover{background:var(--themeLighterAlt)}.flyout .lbl{font-weight:600;margin-top:8px;color:#605e5c;font-size:12px;text-transform:uppercase}
        .main{flex:1;min-width:0}.hubnav{background:var(--themeDarker);color:#fff;display:flex;gap:4px;padding:0 18px;font-size:13px;position:relative;flex-wrap:wrap}.hubnav>span{padding:8px 10px;cursor:pointer;position:relative}.hubnav>span:hover{background:#ffffff1f}
        .hubnav .hubname{font-weight:600}.mega{position:absolute;top:34px;left:0;right:0;background:#fff;color:#323130;box-shadow:0 8px 20px #0003;padding:14px 18px;display:none;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:4px 18px;z-index:9}.mega.open{display:grid}.mega .lbl{font-weight:600;color:#605e5c;font-size:12px}
        .header{display:flex;gap:14px;align-items:center;padding:16px 22px;border-bottom:1px solid #edebe9}.header .logo{width:52px;height:52px;border-radius:8px;background:var(--themePrimary);color:#fff;display:grid;place-items:center;font-weight:700;font-size:20px;flex:none}
        .header .logo.img{background:transparent;width:auto}.header .logo.img img{max-height:52px;max-width:192px}.header h1{margin:0;font-size:22px}.header nav{display:flex;gap:16px;margin-top:4px;font-size:13px;color:#605e5c;flex-wrap:wrap}.header nav span{cursor:pointer}.header nav span:hover{color:var(--themePrimary)}
        .layout-minimal .header{padding:8px 22px}.layout-minimal .header .logo{width:32px;height:32px;font-size:13px}.layout-minimal .header h1{font-size:17px}.layout-extended .header{background:var(--themePrimary);color:#fff;padding:30px 22px}.layout-extended .header nav{color:#ffffffcc}
        .crumbs{padding:8px 22px;font-size:12px;color:#605e5c}.crumbs a{color:var(--themePrimary);cursor:pointer}.status{margin:10px 22px 0;padding:6px 10px;border-radius:6px;font-size:12px;background:var(--themeLighterAlt);border-left:4px solid var(--themePrimary)}
        .status.done{background:#dff6dd;border-left-color:#107c10}.status.failed{background:#fde7e9;border-left-color:#d13438}
        .content{padding:18px 22px 40px;display:flex;flex-direction:column;gap:22px}.content h2{font-size:17px;margin:0 0 8px}
        .hero{display:grid;grid-template-columns:2fr 1fr 1fr;grid-template-rows:120px 120px;gap:6px}.hero div{border-radius:6px;padding:12px;color:#fff;display:flex;align-items:flex-end;font-weight:600;background:linear-gradient(160deg,var(--themeSecondary),var(--themeDarker))}.hero div:first-child{grid-row:span 2;font-size:20px}
        .cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(200px,1fr));gap:12px}.card{border:1px solid #edebe9;border-radius:8px;overflow:hidden;background:#fff}.card .img{height:96px;background:linear-gradient(135deg,var(--themeLight),var(--themeTertiary))}.card .t{padding:8px 10px;font-weight:600}.card .m{padding:0 10px 10px;font-size:12px;color:#605e5c}
        .links{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:8px}.links a{display:flex;gap:8px;align-items:center;padding:10px;border-radius:6px;background:var(--themeLighterAlt);color:inherit;text-decoration:none;cursor:pointer}.links a i{width:22px;height:22px;border-radius:5px;background:var(--themePrimary);display:block;flex:none}
        .list>div{display:flex;gap:10px;align-items:center;padding:8px 0;border-bottom:1px solid #f3f2f1}.list .date{width:46px;text-align:center;border-radius:6px;background:var(--themeLighter);font-weight:700;font-size:12px;padding:4px 0}.list .ico{width:28px;height:34px;border-radius:3px;background:#2b579a;flex:none}.list .ico.x{background:#217346}.list .ico.p{background:#b7472a}
        .people{display:flex;gap:16px;flex-wrap:wrap}.people div{text-align:center;width:96px;font-size:12px}.people i{display:grid;place-items:center;width:56px;height:56px;margin:0 auto 6px;border-radius:50%;background:var(--themeTertiary);color:#fff;font-style:normal;font-weight:700}
        .faq details{border-bottom:1px solid #edebe9;padding:8px 0}.faq summary{cursor:pointer;font-weight:600}.text p{margin:0 0 8px;max-width:760px}.gallery{display:grid;grid-template-columns:repeat(3,1fr);gap:6px}.gallery div{height:110px;border-radius:6px;background:linear-gradient(135deg,var(--themeTertiary),var(--themeDark))}
        .footer{padding:18px 22px;background:var(--neutralLighter,#f3f2f1);font-size:12px;color:#605e5c;display:flex;gap:18px;flex-wrap:wrap}
        #frame.phone .hero{grid-template-columns:1fr;grid-template-rows:140px}#frame.phone .hero div:not(:first-child){display:none}#frame.phone .appbar{display:none}#frame.phone .hubnav>span:not(.hubname){display:none}
        """;

    private const string Script = """
        const S=new Map(D.sites.map(s=>[s.key,s]));const esc=v=>String(v??'').replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
        const root=document.documentElement;for(const[k,v]of Object.entries(D.palette))root.style.setProperty('--'+k,v);
        const key=u=>{if(!u)return null;const k=String(u).replace(/\/$/,'').toLowerCase();return D.aliases[k]||k};
        const people=['Avery Kim','Jordan Lee','Sam Patel','Riley Chen','Morgan Diaz','Casey Novak'];
        function strip(t){return String(t).replace(/ Hub$/,'').replace(/^.* – /,'')}
        function link(u,label){const k=key(u);return `<a data-go="${esc(k||'')}" data-label="${esc(label)}">${esc(label)}</a>`}
        function navTree(nodes){return nodes.map(n=>n.children&&n.children.length?`<div class="lbl">${esc(n.title)}</div>`+n.children.map(c=>c.children&&c.children.length?`<div class="lbl">${esc(c.title)}</div>`+c.children.map(g=>link(g.url,g.title)).join(''):link(c.url,c.title)).join(''):link(n.url,n.title+(n.audience?' 🔒':''))).join('')}
        function section(name,s){const t=strip(s.title);const kids=(s.nav||[]).flatMap(n=>n.children&&n.children.length?n.children.flatMap(c=>c.children&&c.children.length?c.children:[c]):[n]).filter(n=>n.url);
          const siblings=D.sites.filter(x=>x.hub&&x.hub===s.hub&&x.key!==s.key&&x.kind!=='hub');
          switch(name){
            case 'hero':return `<div class="hero"><div>${esc(s.kind==='home'?'Welcome to '+D.organization:t+': what\'s new')}</div><div>${esc(t)} at a glance</div><div>Spotlight</div><div>Get started</div><div>Ask us</div></div>`;
            case 'news':return `<section><h2>News</h2><div class="cards">${[`${t}: this month's highlights`,`Meet the ${t} team`,`Three changes coming to ${t}`].map((x,i)=>`<div class="card"><div class="img"></div><div class="t">${esc(x)}</div><div class="m">Sample news post · ${i+2} days ago</div></div>`).join('')}</div></section>`;
            case 'quicklinks':{const items=(kids.length?kids.slice(0,8).map(n=>link(n.url,n.title)):siblings.length?siblings.slice(0,8).map(x=>link(x.url,strip(x.title))):['Forms','Policies','Contacts','Training'].map(x=>link(null,x))).map(a=>a.replace('<a ','<a ').replace('>','><i></i>'));return `<section><h2>Quick links</h2><div class="links">${items.join('')}</div></section>`}
            case 'events':case 'calendar':return `<section><h2>Events</h2><div class="list">${[['12','NOV',`${t} town hall`],['19','NOV',`Ask ${t} anything`],['03','DEC',`${t} onboarding session`]].map(e=>`<div><span class="date">${e[0]}<br>${e[1]}</span><span>${esc(e[2])}<br><small class="m">Sample event · Teams</small></span></div>`).join('')}</div></section>`;
            case 'documents':return `<section><h2>Documents</h2><div class="list">${[['',`${t} policy`],['x',`${t} budget overview`],['p',`${t} introduction deck`],['',`How to request ${t} services`]].map(d=>`<div><span class="ico ${d[0]}"></span><span>${esc(d[1])}<br><small>Sample document · edited last week</small></span></div>`).join('')}</div></section>`;
            case 'people':return `<section><h2>People</h2><div class="people">${people.slice(0,4).map(p=>`<div><i>${p.split(' ').map(w=>w[0]).join('')}</i>${esc(p)}<br><small>${esc(t)}</small></div>`).join('')}</div></section>`;
            case 'faq':return `<section class="faq"><h2>Frequently asked questions</h2>${[`Who do I contact about ${t}?`,`Where are the ${t} forms?`,`How do I get access?`].map(q=>`<details><summary>${esc(q)}</summary><p>Sample answer: replace with your own.</p></details>`).join('')}</section>`;
            case 'gallery':case 'video':return `<section><h2>${name==='video'?'Video':'Gallery'}</h2><div class="gallery"><div></div><div></div><div></div></div></section>`;
            case 'conversations':return `<section><h2>Conversations</h2><div class="list">${['Welcome to the new site!','Where should we keep meeting notes?'].map(c=>`<div><span class="date">💬</span><span>${esc(c)}<br><small>Sample post</small></span></div>`).join('')}</div></section>`;
            case 'tasks':return `<section><h2>Tasks</h2><div class="list">${['Review the plan','Upload team documents','Invite members'].map(c=>`<div><span class="date">☐</span><span>${esc(c)}</span></div>`).join('')}</div></section>`;
            case 'text':return `<section class="text"><h2>About ${esc(t)}</h2><p>${esc(s.purpose||'What this site is for.')}</p><p>Sample text: say who this site is for, what they find here and who to contact.</p></section>`;
            default:return '';}}
        function hubOf(s){return s.hub?D.sites.find(x=>x.kind==='hub'&&x.hub===s.hub):null}
        function page(k,label){const s=S.get(k);const home=S.get(D.home);
          if(!s)return {title:label||'Page',html:pagePlaceholder(label||'Page')};
          const hub=hubOf(s);const nav=(hub&&hub.nav)||[];
          let h='';
          if(hub||s.kind==='hub'){const hn=hub||s;h+=`<div class="hubnav"><span class="hubname" data-go="${esc(hn.key)}">${esc(strip(hn.title))}</span>${(hn.nav||[]).filter(n=>n.title!=='Home').map((n,i)=>`<span data-menu="${i}">${esc(n.title)}${n.children&&n.children.length?' ▾':''}</span>`).join('')}<div class="mega" id="mega"></div></div>`}
          const initials=D.organization.split(' ').slice(0,2).map(w=>w[0]).join('').toUpperCase();
          h+=`<div class="header"><div class="logo${D.logo?' img':''}">${D.logo?`<img alt="logo" src="${esc(D.logo)}">`:esc(initials)}</div><div><h1>${esc(s.title)}</h1><nav>${['Home',...(s.links&&s.links.length?s.links:['News','Documents','Pages'])].map(x=>`<span>${esc(x)}</span>`).join('')}</nav></div></div>`;
          const crumbs=[home&&s.key!==home.key?`<a data-go="${esc(home.key)}">${esc(home.title)}</a>`:'' ,hub&&hub.key!==s.key?`<a data-go="${esc(hub.key)}">${esc(hub.title)}</a>`:''].filter(Boolean);
          h+=`<div class="crumbs">${crumbs.join(' › ')}${crumbs.length?' › ':''}${esc(s.title)}</div>`;
          const note=[s.existing?'Existing site: its content stays; this page shows sample content in the new theme and navigation.':`New ${s.kind==='team'?'team':'communication'} site${s.template?' from the '+esc(s.template)+' template':''}.`,s.design?`Design: ${esc(s.design)} (Hubwright's wireframe, sample content).`:'',s.status?`Implementation: ${esc(s.status)}.`:''].join(' ');
          h+=`<div class="status ${s.status==='Done'?'done':s.status==='Failed'?'failed':''}">${note}</div>`;
          h+=`<div class="content">${s.sections.map(x=>section(x,s)).join('')}${s.kind==='hub'?hubSites(s):''}</div>`;
          h+=`<div class="footer"><b>${esc(D.organization)}</b><span>Contact us</span><span>Accessibility</span><span>Privacy</span><span>Sample footer</span></div>`;
          return {title:s.title,html:h}}
        function hubSites(hub){const list=D.sites.filter(x=>x.hub===hub.hub&&x.kind!=='hub');if(!list.length)return '';return `<section><h2>Sites in ${esc(strip(hub.title))}</h2><div class="cards">${list.map(x=>`<div class="card" data-go="${esc(x.key)}" style="cursor:pointer"><div class="img"></div><div class="t">${esc(x.title)}</div><div class="m">${x.existing?'Existing site':'New site'}${x.template?' · '+esc(x.template):''}</div></div>`).join('')}</div></section>`}
        function pagePlaceholder(label){return `<div class="header"><div class="logo">${esc(D.organization[0])}</div><div><h1>${esc(label)}</h1></div></div><div class="content"><section class="text"><h2>${esc(label)}</h2><p>A page in the navigation. Sample content: the real page is written when the site is built.</p></section></div>`}
        function chrome(inner){return `<div class="suite"><span class="waffle">⋮⋮⋮</span><span>SharePoint</span><span class="search">Search this site</span></div><div class="body"><div class="appbar"><button data-go="${esc(D.home)}" title="Home">⌂</button><button id="gnBtn" title="Global navigation">☰</button><button title="My sites">▦</button><button title="News">📰</button><div class="flyout" id="gn"><h4>${esc(D.organization)}</h4>${navTree(D.global)}</div></div><div class="main layout-${esc(D.layout)}">${inner}</div></div>`}
        function render(){const params=new URLSearchParams(location.hash.slice(1));const k=params.get('site')||D.start||D.home;const p=page(k,params.get('label'));
          const el=document.getElementById('page');el.innerHTML=chrome(p.html);document.title=p.title+' · '+D.organization+' intranet preview';
          const jump=document.getElementById('jump');jump.value=S.has(k)?k:'';
          el.querySelectorAll('[data-go]').forEach(a=>a.onclick=e=>{e.stopPropagation();go(a.dataset.go,a.dataset.label)});
          const gn=document.getElementById('gn');document.getElementById('gnBtn').onclick=e=>{e.stopPropagation();gn.classList.toggle('open')};
          const s=S.get(k);const hub=s&&(s.kind==='hub'?s:hubOf(s));const mega=document.getElementById('mega');
          el.querySelectorAll('[data-menu]').forEach(m=>m.onclick=e=>{e.stopPropagation();const n=(hub.nav||[]).filter(n=>n.title!=='Home')[+m.dataset.menu];
            if(!n.children||!n.children.length){go(key(n.url),n.title);return}
            mega.innerHTML=navTree(n.children.length&&n.children.every(c=>!c.children||!c.children.length)?[{title:n.title,children:n.children}]:n.children);mega.classList.toggle('open');
            mega.querySelectorAll('[data-go]').forEach(a=>a.onclick=ev=>{ev.stopPropagation();go(a.dataset.go,a.dataset.label)})});
          el.onclick=()=>{gn.classList.remove('open');if(mega)mega.classList.remove('open')};
          window.scrollTo(0,0);updateButtons()}
        function go(k,label){const target=k&&S.has(k)?'site='+encodeURIComponent(k):'site='+encodeURIComponent(k||'')+'&label='+encodeURIComponent(label||'Page');if(location.hash.slice(1)!==target)location.hash=target;else render()}
        let depth=0,maxDepth=0;window.addEventListener('hashchange',()=>{render()});
        function updateButtons(){document.getElementById('back').disabled=history.length<=1}
        document.getElementById('back').onclick=()=>history.back();document.getElementById('fwd').onclick=()=>history.forward();document.getElementById('homeBtn').onclick=()=>go(D.home);
        document.getElementById('mobile').onclick=()=>document.getElementById('frame').classList.toggle('phone');
        const jump=document.getElementById('jump');jump.innerHTML=D.sites.map(s=>`<option value="${esc(s.key)}">${s.kind==='home'?'🏠':s.kind==='hub'?'🧭':s.kind==='team'?'👥':'📄'} ${esc(s.title)}</option>`).join('');jump.onchange=()=>go(jump.value);
        window.addEventListener('message',e=>{if(e.data&&e.data.hubwright==='open'&&e.data.url)go(key(e.data.url))});
        window.hubwrightOpen=u=>go(key(u));
        render();
        """;
}
