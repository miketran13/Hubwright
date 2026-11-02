using System.Globalization;
using System.Text;
using Hubwright.Snapshot;
using Hubwright.Theming;

namespace Hubwright.Blueprints;

/// <summary>
/// Turns questionnaire answers into a first blueprint, deterministically. It follows Microsoft's
/// intranet guidance: a flat structure of one site per unit of work, hubs for functions with several
/// services, a home site with global navigation in the app bar, private team sites kept out of hub
/// navigation, extranets kept out of hubs, two owners per site, and links shared inside the
/// organization by default. The AI architect refines the result; it does not start from nothing.
/// </summary>
public static class BlueprintGenerator
{
    // Sub-functions from Microsoft's hub planning guidance (HR) and common intranet patterns.
    private static readonly Dictionary<string, string[]> KnownFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HR"] = ["Benefits", "Pay and compensation", "Talent acquisition", "Learning", "Manager portal"],
        ["Human Resources"] = ["Benefits", "Pay and compensation", "Talent acquisition", "Learning", "Manager portal"],
        ["IT"] = ["Service desk", "Security", "Devices and software"],
        ["Finance"] = ["Expenses", "Procurement", "Budget and planning"],
        ["Legal"] = ["Contracts", "Compliance", "Policies"],
        ["Communications"] = ["Brand and templates", "Events", "Newsroom"],
        ["Sales"] = ["Sales enablement", "Pricing", "Customer stories"],
        ["Marketing"] = ["Campaigns", "Brand and templates", "Market research"],
        ["Operations"] = ["Facilities", "Health and safety", "Travel"],
        ["Engineering"] = ["Engineering handbook", "Architecture", "Release notes"],
    };

    public static Blueprint Generate(IntranetAnswers answers)
    {
        var org = answers.Organization.Trim();
        var primary = Rgb.Parse(answers.BrandColor);
        var accessible = ThemeGenerator.SuggestAccessiblePrimary(primary, Rgb.White) ?? primary;
        var blueprint = new Blueprint
        {
            Name = $"{org} intranet",
            Organization = org,
            TenantPrefix = Slug(answers.TenantPrefix),
            BrandColor = accessible.Hex,
            HeaderLayout = answers.HeaderLayout,
            Languages = answers.Languages,
            Home = new BlueprintSite { Title = $"{org} Home", Slug = "home", Purpose = "The front door: company news, key links, search and the global navigation." },
        };

        if (!accessible.Equals(primary))
        {
            blueprint.Rationale.Add($"Brand colour {primary.Hex} is below 4.5:1 contrast as text or behind white button labels; using {accessible.Hex}, the nearest lightness that passes WCAG 2.1 AA.");
        }

        var departments = answers.Departments.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var small = answers.Employees < 100 || departments.Count <= 2;

        if (small)
        {
            var hub = new BlueprintHub { Title = "Departments Hub", Slug = "departments", Purpose = "One family for every department; small enough that one shared navigation fits." };
            hub.Sites.AddRange(departments.Select(d => new BlueprintSite { Title = d, Slug = Slug(d), Purpose = $"{d}: services, policies and contacts for everyone." }));
            blueprint.Hubs.Add(hub);
            blueprint.Rationale.Add("Small organization: one hub keeps a single navigation; Microsoft notes a single hub loses scoped news and search, so split it when departments grow.");
        }
        else
        {
            foreach (var department in departments)
            {
                var hub = new BlueprintHub
                {
                    Title = $"{department} Hub",
                    Slug = Slug(department) + "-hub",
                    Purpose = $"Connects the {department} family of sites: shared navigation, rolled-up news and its own search scope.",
                };
                var functions = KnownFunctions.TryGetValue(department, out var known) ? known : ["Services", "Policies and guides"];
                hub.Sites.AddRange(functions.Select(f => new BlueprintSite { Title = $"{department} – {f}", Slug = Slug($"{department}-{f}"), Purpose = $"{f} for the whole organization, owned by {department}." }));
                if (answers.HasGoal("collaboration"))
                {
                    hub.Sites.Add(new BlueprintSite { Title = $"{department} Team", Slug = Slug(department) + "-team", Kind = SiteKind.TeamGroup, Private = true, Purpose = $"The {department} team's own workspace, connected to Teams." });
                }

                blueprint.Hubs.Add(hub);
            }

            blueprint.Rationale.Add("One hub per function: Microsoft recommends a hub when a function offers several logically different services, with one site per unit of work instead of subsites.");
            blueprint.Rationale.Add("Hub names follow one convention (\"<Function> Hub\") so people recognise hubs in search and navigation.");
        }

        if (answers.Regions.Count > 1)
        {
            var regions = new BlueprintHub { Title = "Locations Hub", Slug = "locations-hub", Purpose = "Office and regional sites: local news, facilities and people." };
            regions.Sites.AddRange(answers.Regions.Select(r => new BlueprintSite { Title = $"{r} Office", Slug = Slug(r) + "-office", Purpose = $"News, facilities and contacts for {r}." }));
            blueprint.Hubs.Add(regions);
            blueprint.Rationale.Add("Regions get their own hub and every function stays functional: Microsoft advises one consistent approach for all functions that have a regional pattern.");
        }

        if (answers.HasGoal("onboarding"))
        {
            AddToFirstHub(blueprint, "HR", new BlueprintSite { Title = "New starters", Slug = "new-starters", Purpose = "Onboarding: first-week checklist, who's who, tools and training." });
        }

        if (answers.HasGoal("communities"))
        {
            blueprint.Standalone.Add(new BlueprintSite { Title = "Communities", Slug = "communities", Purpose = "Interest and practice communities, with a Viva Engage community connected." });
        }

        if (answers.ExternalCollaboration)
        {
            blueprint.Standalone.Add(new BlueprintSite { Title = "Partner extranet", Slug = "partners", Kind = SiteKind.TeamGroup, Extranet = true, Purpose = "Work with partners. Not associated with a hub, so guests never see internal navigation." });
            blueprint.Rationale.Add("The extranet is not associated with any hub: Microsoft's tip is not to associate extranet sites when you don't want extranet users to see the shared navigation.");
        }

        // Home site: a hub only when there is one family; otherwise the app bar is the global navigation.
        blueprint.HomeIsHub = false;
        blueprint.Rationale.Add(blueprint.Hubs.Count > 1
            ? "The home site is not a hub: with several hubs, Microsoft suggests a regular home site and the SharePoint app bar for global navigation."
            : "The home site stays a regular site; the app bar carries global navigation either way.");

        foreach (var hub in blueprint.Hubs)
        {
            hub.Navigation = HubNavigation(blueprint, hub);
        }

        blueprint.GlobalNavigation = GlobalNavigation(blueprint, answers);
        blueprint.Policy = Policy(answers);
        blueprint.Rationale.Add("Sharing defaults to links for people in the organization; Anyone links, if allowed, expire and are view-only.");
        blueprint.Rationale.Add("Every site gets a sensitivity label (General, Confidential for private team sites, Partner for extranets), as Microsoft's Copilot readiness guidance flags unlabeled sites.");
        blueprint.Rationale.Add("Every site has at least two owners, and inactive-site, ownership and attestation policies are all on (Microsoft's recommended governance strategy combines all three).");
        return blueprint;
    }

    private static void AddToFirstHub(Blueprint blueprint, string department, BlueprintSite site)
    {
        var hub = blueprint.Hubs.FirstOrDefault(h => h.Title.StartsWith(department, StringComparison.OrdinalIgnoreCase)) ?? blueprint.Hubs.FirstOrDefault();
        hub?.Sites.Add(site);
    }

    private static List<NavNode> HubNavigation(Blueprint blueprint, BlueprintHub hub)
    {
        var publicSites = hub.Sites.Where(s => !s.Private).ToList();
        var nav = new List<NavNode> { new() { Title = "Home", Url = blueprint.Url(hub.Slug) } };
        if (publicSites.Count > 6)
        {
            // Three levels make a mega menu worthwhile: group the sites under one label.
            nav.Add(new NavNode
            {
                Title = "Services",
                Children = [new NavNode { Title = hub.Title.Replace(" Hub", "", StringComparison.Ordinal), Children = [.. publicSites.Select(s => new NavNode { Title = ShortTitle(s.Title), Url = blueprint.Url(s.Slug) })] }],
            });
        }
        else
        {
            nav.AddRange(publicSites.Select(s => new NavNode { Title = ShortTitle(s.Title), Url = blueprint.Url(s.Slug) }));
        }

        nav.AddRange(hub.Sites.Where(s => s.Private).Select(s => new NavNode { Title = ShortTitle(s.Title), Url = blueprint.Url(s.Slug), Restricted = true, AudienceTargeted = true }));
        return nav;
    }

    private static List<NavNode> GlobalNavigation(Blueprint blueprint, IntranetAnswers answers)
    {
        var nav = new List<NavNode> { new() { Title = "Home", Url = blueprint.Url(blueprint.Home.Slug) } };
        if (answers.HasGoal("news"))
        {
            nav.Add(new NavNode { Title = "News", Url = blueprint.Url(blueprint.Home.Slug) + "/_layouts/15/news.aspx" });
        }

        nav.Add(new NavNode
        {
            Title = "Working here",
            Children = [.. blueprint.Hubs.Select(h => new NavNode { Title = h.Title.Replace(" Hub", "", StringComparison.Ordinal), Url = blueprint.Url(h.Slug) })],
        });

        if (answers.HasGoal("policies"))
        {
            nav.Add(new NavNode { Title = "Policies and procedures", Url = blueprint.Url(blueprint.Home.Slug) + "/SitePages/Policies.aspx" });
        }

        nav.Add(new NavNode { Title = "Tools and resources", Url = blueprint.Url(blueprint.Home.Slug) + "/SitePages/Tools.aspx" });
        return nav;
    }

    private static BlueprintPolicy Policy(IntranetAnswers answers) => new()
    {
        Sharing = answers.AllowAnyoneLinks ? SharingLevel.Anyone : answers.ExternalCollaboration ? SharingLevel.NewAndExistingGuests : SharingLevel.ExistingGuests,
        DefaultLink = DefaultLinkType.Organization,
        AnyoneLinkExpirationDays = 30,
        GuestAccessExpirationDays = 90,
        MinimumOwners = 2,
        InactiveMonths = 6,
    };

    private static string ShortTitle(string title)
    {
        var dash = title.IndexOf(" – ", StringComparison.Ordinal);
        return dash > 0 ? title[(dash + 3)..] : title;
    }

    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        return sb.ToString().Trim('-');
    }
}
