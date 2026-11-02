using Hubwright.Blueprints;
using Hubwright.Proposals;

namespace Hubwright.Implementation;

/// <summary>
/// Launch steps: what turns a set of sites into an intranet people use. Organization news, a draft
/// welcome post per hub, the brand center, multilingual sites, audience-targeted navigation, the
/// SharePoint app in Teams dashboard and page templates. Only organization news and the draft posts
/// are automatic (PnP PowerShell; nothing is published); the rest are guided.
/// </summary>
public static partial class ImplementationPlanner
{
    public const string PnpOrgNewsDoc = "https://pnp.github.io/powershell/cmdlets/Add-PnPOrgNewsSite.html";
    public const string PnpAddPageDoc = "https://pnp.github.io/powershell/cmdlets/Add-PnPPage.html";
    public const string BrandCenterDoc = "https://learn.microsoft.com/en-us/sharepoint/brand-center-overview";
    public const string MultilingualDoc = "https://support.microsoft.com/en-us/office/create-multilingual-communication-sites-pages-and-news-2bb7d610-5453-41c6-a0e8-6f40b3ed750c";
    public const string DashboardDoc = "https://learn.microsoft.com/en-us/sharepoint/homesites/create-dashboard";

    /// <summary>Marks sample text everywhere it is created, so nobody publishes it by accident.</summary>
    public const string SampleMarker = "[Sample text from Hubwright: replace it before you publish.]";

    private static void AddLaunch(ImplementationPlan plan, IntranetProposal proposal, Blueprint b, string admin, string homeUrl, List<string> homeDepends, string phase)
    {
        var steps = plan.Steps;
        var hubs = b.Hubs.Select(h => (Hub: h, Url: h.ExistingUrl ?? b.Url(h.Slug), Depends: h.ExistingUrl is null && plan.Find("hub-" + h.Slug) is not null ? new List<string> { "hub-" + h.Slug } : [])).ToList();

        // Organization news: the home site is one automatically; hubs publish department news.
        foreach (var (hub, url, depends) in hubs)
        {
            steps.Add(new ImplementationStep
            {
                Id = "news-" + hub.Slug,
                Phase = phase,
                Title = $"Make \"{hub.Title}\" an organization news site",
                Detail = "Its news posts get the organization's news styling and appear on the SharePoint start page. The home site already is one.",
                Kind = StepKind.Automatic,
                Risk = StepRisk.Changes,
                Operation = StepOperation.AddOrgNewsSite,
                Target = url,
                DependsOn = depends,
                Source = PnpOrgNewsDoc,
                GuideUrl = $"{admin}/_layouts/15/online/AdminHome.aspx",
                Instructions = ["In PowerShell as a SharePoint Administrator:", $"Set-SPOOrgNewsSite -OrgNewsSiteUrl {url}"],
                Hint = "org-news",
            });
        }

        // A draft welcome post on the home site and every hub, so owners start from an example.
        foreach (var (title, slug, url, purpose, depends) in new[] { (b.Home.Title, "home", homeUrl, b.Home.Purpose, homeDepends) }.Concat(hubs.Select(h => (h.Hub.Title, h.Hub.Slug, h.Url, h.Hub.Purpose, h.Depends))))
        {
            steps.Add(new ImplementationStep
            {
                Id = "welcome-" + slug,
                Phase = phase,
                Title = $"Draft a welcome news post on \"{title}\"",
                Detail = "A draft (not published) news post with sample text, for the site owner to rewrite and publish. A page called Welcome that already exists is left alone.",
                Kind = StepKind.Automatic,
                Risk = StepRisk.Adds,
                Operation = StepOperation.AddStarterPage,
                Target = url,
                DependsOn = depends,
                Parameters =
                {
                    ["name"] = "Welcome",
                    ["text"] = $"<p>{SampleMarker}</p><p>Welcome to {title}. {purpose}</p><p>Start here for news, people and resources from {title}.</p>",
                },
                Source = PnpAddPageDoc,
                GuideUrl = url,
                Instructions = [$"On {url}: New → News post → title Welcome → write the welcome → Save as draft."],
                Hint = "starter-content",
            });
        }

        steps.Add(Guide("page-templates", phase, "Save news and page templates for each hub", homeUrl,
            [
                "On each hub, open the Welcome draft → finish the layout (no sample text) → Save as template.",
                "Do the same for a standard page (for example a policy or a team page).",
                "Site owners then start from New → News post (or Page) → the template.",
                .. hubs.Select(h => $"{h.Hub.Title}: {h.Url}"),
            ], StepRisk.Adds, [], dependsOn: [.. hubs.Select(h => "welcome-" + h.Hub.Slug)]).With(s => s.Hint = "page-templates"));

        steps.Add(Guide("brand-center", phase, "Set up the brand center (Global Administrator)", "https://admin.microsoft.com/",
            [
                "Microsoft 365 admin center → Settings → Org settings → Brand center.",
                $"Name the site (for example \"{b.Organization} brand\") and create it, or confirm your existing organization assets site.",
                "Turn on the public CDN only if you'll use brand fonts.",
                $"In the brand center app: add the brand colour {b.BrandColor}, the logo and approved images.",
                "Add the brand managers as owners of the brand center site.",
            ], StepRisk.Changes, []).With(s =>
            {
                s.Hint = "brand-center";
                s.Source = BrandCenterDoc;
            }));

        var languages = proposal.Answers.Intranet.Languages.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (languages.Count > 1)
        {
            steps.Add(Guide("multilingual", phase, $"Turn on translation into {string.Join(", ", languages.Skip(1))}", homeUrl,
                [
                    "On the home site and each hub: Settings → Site information → View all site settings → Language settings.",
                    $"Enable translation into multiple languages; add {string.Join(", ", languages.Skip(1))}; assign a translator for each.",
                    "Then translate the navigation, the welcome posts and the most-read pages.",
                    $"Home: {homeUrl}",
                    .. hubs.Select(h => $"{h.Hub.Title}: {h.Url}"),
                ], StepRisk.Changes, [], dependsOn: [.. homeDepends]).With(s =>
                {
                    s.Hint = "multilingual";
                    s.Source = MultilingualDoc;
                }));
        }

        var privateLinks = b.Hubs.Where(h => h.Sites.Any(s => s.Private)).ToList();
        if (privateLinks.Count > 0)
        {
            steps.Add(Guide("audience-targeting", phase, $"Target private sites' links in {privateLinks.Count} hub navigation(s)", b.Url(privateLinks[0].Slug),
                [
                    "On the hub: Edit (navigation) → Enable site navigation audience targeting → Save.",
                    "Edit each private site's link → Audiences to target → the site's members group.",
                    .. privateLinks.SelectMany(h => h.Sites.Where(s => s.Private).Select(s => $"{h.Title} → {s.Title}")),
                ], StepRisk.Changes, ["hubs.private-links", "nav.private-links"]).With(s => s.Hint = "audience-targeting"));
        }

        steps.Add(Guide("teams-dashboard", phase, "Set up the SharePoint app in Teams (dashboard, resources, feed)", homeUrl,
            [
                "Needs the home site (Hubwright sets it in the Structure phase).",
                "In Teams: Apps → SharePoint app (Viva Connections) → Edit the dashboard → add cards for the top tasks; target them by role or region.",
                "Resources: reuse the global navigation. Feed: news from the home site and the organization news sites.",
                "Admins: pin the app in Teams (Teams admin center → Setup policies).",
            ], StepRisk.Adds, ["home.missing"], dependsOn: ["set-home-site"]).With(s =>
            {
                s.Hint = "teams-dashboard";
                s.Source = DashboardDoc;
            }));
    }
}
