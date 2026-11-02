using System.Text.Json;
using Hubwright.Blueprints;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Templates;

namespace Hubwright.Implementation;

/// <summary>
/// Turns a proposal into an implementation plan, deterministically. Only operations with a
/// documented Microsoft API become automatic steps; everything else is a guided step with the page
/// to open and the values to enter. Nothing that deletes, archives or unregisters is ever automatic.
/// </summary>
public static partial class ImplementationPlanner
{
    public const string SiteCreationDoc = "https://learn.microsoft.com/en-us/sharepoint/dev/apis/site-creation-rest";
    public const string HubDoc = "https://learn.microsoft.com/en-us/sharepoint/dev/features/hub-site/hub-site-rest-api";
    public const string ThemeDoc = "https://learn.microsoft.com/en-us/sharepoint/dev/declarative-customization/site-theming/sharepoint-site-theming-rest-api";
    public const string SharingDoc = "https://learn.microsoft.com/en-us/graph/api/sharepointsettings-update";
    public const string PnpHomeSiteDoc = "https://pnp.github.io/powershell/cmdlets/Set-PnPHomeSite.html";
    public const string PnpNavigationDoc = "https://pnp.github.io/powershell/cmdlets/Add-PnPNavigationNode.html";
    public const string PnpSiteDesignDoc = "https://pnp.github.io/powershell/cmdlets/Invoke-PnPSiteDesign.html";
    public const string PnpWebHeaderDoc = "https://pnp.github.io/powershell/cmdlets/Set-PnPWebHeader.html";
    public const string PnpLookBookDoc = "https://learn.microsoft.com/en-us/sharepoint/dev/solution-guidance/applying-pnp-templates";
    public const string OrgThemeDoc = "https://learn.microsoft.com/en-us/microsoft-365/admin/setup/customize-your-organization-theme";

    public static ImplementationPlan Build(IntranetProposal proposal, TenantSnapshot snapshot, IReadOnlyList<SiteTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(templates);
        var b = proposal.Blueprint;
        var root = $"https://{b.TenantPrefix}.sharepoint.com";
        var admin = $"https://{b.TenantPrefix}-admin.sharepoint.com";
        var plan = new ImplementationPlan { Proposal = proposal.Name, Tenant = proposal.Tenant, RootUrl = root };
        var steps = plan.Steps;
        var byId = templates.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var themeName = b.Theme().Name;
        var themeJson = ThemeJson(b);
        var hubIdByUrl = snapshot.Hubs.Where(h => Guid.TryParse(h.Id, out _) && !string.IsNullOrWhiteSpace(h.SiteUrl))
            .GroupBy(h => h.SiteUrl.TrimEnd('/'), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var siteUrlBySlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);


        // 1 · Risks
        const string risks = "1 · Close the risks";
        steps.Add(new ImplementationStep
        {
            Id = "sharing",
            Phase = risks,
            Title = $"Set tenant sharing to {IntranetProposalGenerator.Human(b.Policy.Sharing)}",
            Detail = $"Today: {(snapshot.Settings.SharingLevel is { } level ? IntranetProposalGenerator.Human(level) : "not read")}. Sites can be stricter than the tenant, never looser.",
            Kind = StepKind.Automatic,
            Risk = StepRisk.Changes,
            Operation = StepOperation.SetSharing,
            Parameters = { ["sharingCapability"] = SharingCapability(b.Policy.Sharing) },
            Source = SharingDoc,
            Rules = ["sharing.most-permissive"],
        });
        steps.Add(Guide("sharing-links", risks, "Default links and Anyone link expiry", $"{admin}/_layouts/15/online/AdminHome.aspx#/sharing",
            [
                "SharePoint admin center → Policies → Sharing.",
                "File and folder links → default: Specific people or Only people in your organization.",
                b.Policy.Sharing == SharingLevel.Anyone ? $"Anyone links: expire within {b.Policy.AnyoneLinkExpirationDays} days; files View, folders View." : "Anyone links are off at this sharing level.",
                b.Policy.Sharing != SharingLevel.OnlyOrganization ? $"Guest access to a site or OneDrive expires after {b.Policy.GuestAccessExpirationDays} days." : "Guests are off at this sharing level.",
            ], StepRisk.Changes, ["sharing.default-link", "sharing.anyone-expiry"]));

        var open = proposal.Sites.Where(s => s.Actions.Contains(SiteAction.ReviewPermissions)).ToList();
        if (open.Count > 0)
        {
            steps.Add(Guide("permissions", risks, $"Review access on {open.Count} site(s) open to everyone", open[0].Url + "/_layouts/15/user.aspx",
                ["Remove 'Everyone' and 'Everyone except external users' grants; use the site's Members and Visitors groups.", "Check each site's sharing links (Site settings → Site permissions).", .. open.Take(25).Select(s => $"{s.Title}: {s.Url}/_layouts/15/user.aspx")],
                StepRisk.Changes, ["perm.everyone", "sharing.anyone-links-on-site"]));
        }

        var ownerless = proposal.Sites.Where(s => s.Actions.Contains(SiteAction.AddOwners)).ToList();
        if (ownerless.Count > 0)
        {
            steps.Add(Guide("owners", risks, $"Give {ownerless.Count} site(s) a second owner", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement",
                ["Active sites → select the site → Membership → Site admins / Owners → add.", .. ownerless.Take(25).Select(s => $"{s.Title}: {s.Url}")],
                StepRisk.Changes, ["perm.ownerless", "perm.single-owner"]));
        }

        var unread = proposal.Sites.Where(s => s.Actions.Contains(SiteAction.CheckUnread)).ToList();
        if (unread.Count > 0)
        {
            steps.Add(Guide("unread", risks, $"Check {unread.Count} site(s) the scan could not read", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement",
                ["You are not a member of these sites, so their owners, permissions and content were not assessed.", "Active sites → select the site → Membership and Settings show its owners and sharing without joining it.", "Or ask each owner to check owners, Everyone grants and sharing links. Don't add yourself as a site admin just to scan.", .. unread.Take(25).Select(s => $"{s.Title}: {s.Url}")],
                StepRisk.Changes, []));
        }

        // 2 · Structure
        const string structure = "2 · Build the structure";
        steps.Add(new ImplementationStep
        {
            Id = "theme",
            Phase = structure,
            Title = $"Add the theme \"{themeName}\" to the tenant",
            Detail = $"Brand colour {b.BrandColor}; every text pair checked against WCAG 2.1 AA. Updated in place if a theme with this name exists.",
            Kind = StepKind.Automatic,
            Risk = StepRisk.Adds,
            Operation = StepOperation.AddOrUpdateTheme,
            Target = root,
            Parameters = { ["name"] = themeName, ["themeJson"] = themeJson },
            Source = ThemeDoc,
            Rules = ["brand.contrast"],
        });

        string CreateSite(BlueprintSite site, string id, bool communication, string? dependsOnHub)
        {
            var url = site.ExistingUrl ?? b.Url(site.Slug);
            siteUrlBySlug[site.Slug] = url;
            if (site.ExistingUrl is not null)
            {
                return url;
            }

            var template = site.Template is { } t && byId.TryGetValue(t, out var found) ? found : null;
            if (template?.Source == TemplateSource.PnpPackage)
            {
                // The package creates the site itself, with its pages and navigation (PnP PowerShell).
                var package = new ImplementationStep
                {
                    Id = id,
                    Phase = structure,
                    Title = $"Provision \"{site.Title}\" from the look book package \"{template.Name}\"",
                    Detail = "The package creates the site with its own pages, navigation and sample content. Replace the sample content before launch.",
                    Kind = StepKind.Automatic,
                    Risk = StepRisk.Adds,
                    Operation = StepOperation.ProvisionPackage,
                    Target = url,
                    Parameters = { ["package"] = RawPackage(template.Package ?? "") },
                    Source = PnpLookBookDoc,
                    GuideUrl = template.Package,
                    Instructions =
                    [
                        "A SharePoint admin runs this in PowerShell 7 with PnP PowerShell (download the package first):",
                        $"Connect-PnPOnline {root} -Interactive -ClientId <Hubwright app id>",
                        $"Invoke-PnPTenantTemplate -Path .\\{Path.GetFileName(template.Package ?? "template.pnp")} -Parameters @{{{string.Join(";", template.Parameters.Select(p => $"\"{p}\"=\"{PackageValue(p, site)}\""))}}}",
                    ],
                };
                foreach (var parameter in template.Parameters)
                {
                    package.Parameters["p:" + parameter] = PackageValue(parameter, site);
                }

                steps.Add(package);
            }            else
            {
            var step = new ImplementationStep
            {
                Id = id,
                Phase = structure,
                Title = $"Create {(communication ? "communication" : "team")} site \"{site.Title}\"",
                Detail = site.Purpose,
                Kind = StepKind.Automatic,
                Risk = StepRisk.Adds,
                Operation = StepOperation.CreateSite,
                Target = url,
                Parameters =
                {
                    ["title"] = site.Title,
                    ["url"] = url,
                    ["description"] = site.Purpose,
                    ["webTemplate"] = communication ? "SITEPAGEPUBLISHING#0" : "STS#3",
                    ["lcid"] = Lcid(b.Languages.FirstOrDefault()),
                },
                Source = SiteCreationDoc,
            };
            if (communication && template?.SiteDesignId is { } design)
            {
                step.Parameters["siteDesignId"] = design;
            }

            steps.Add(step);

            }

            if (template?.PnpTemplate is { } builtIn && template.SiteDesignId is null && template.Id is not ("standard-team" or "standard-communication"))
            {
                steps.Add(new ImplementationStep
                {
                    Id = id + "-template",
                    Phase = structure,
                    Title = $"Apply the \"{template.Name}\" template to \"{site.Title}\"",
                    Detail = "Microsoft's ready-made pages and news templates for this kind of site, applied before any content is added.",
                    Kind = StepKind.Automatic,
                    Risk = StepRisk.Adds,
                    Operation = StepOperation.ApplySiteTemplate,
                    Target = url,
                    Parameters = { ["template"] = builtIn },
                    DependsOn = [id],
                    Source = PnpSiteDesignDoc,
                    GuideUrl = url,
                    Instructions = [$"On {url}: Settings (gear) → Apply a site template → Microsoft → {template.Name} → Use template."],
                });
            }

            steps.Add(new ImplementationStep
            {
                Id = id + "-header",
                Phase = structure,
                Title = $"Colour the header of \"{site.Title}\" ({b.HeaderLayout} layout)",
                Detail = "SharePoint shows a white header until its background emphasis is set; Strong uses the theme's colour.",
                Kind = StepKind.Automatic,
                Risk = StepRisk.Adds,
                Operation = StepOperation.SetSiteHeader,
                Target = url,
                Parameters = { ["layout"] = Layout(b.HeaderLayout), ["emphasis"] = "Strong" },
                DependsOn = [id],
                Source = PnpWebHeaderDoc,
                GuideUrl = url,
                Instructions = [$"On {url}: Settings (gear) → Change the look → Header → Layout: {Layout(b.HeaderLayout)}, Background: the strongest theme colour → Save."],
            });
            steps.Add(LogoStepFor(b, id, site.Title, url, structure));

            steps.Add(new ImplementationStep
            {
                Id = id + "-theme",
                Phase = structure,
                Title = $"Apply the theme to \"{site.Title}\"",
                Kind = StepKind.Automatic,
                Risk = StepRisk.Adds,
                Operation = StepOperation.ApplyTheme,
                Target = url,
                Parameters = { ["name"] = themeName, ["themeJson"] = themeJson },
                DependsOn = ["theme", id],
                Source = ThemeDoc,
            });
            if (dependsOnHub is not null)
            {
                steps.Add(new ImplementationStep
                {
                    Id = id + "-hub",
                    Phase = structure,
                    Title = $"Associate \"{site.Title}\" with its hub",
                    Kind = StepKind.Automatic,
                    Risk = StepRisk.Adds,
                    Operation = StepOperation.AssociateWithHub,
                    Target = url,
                    Parameters = { ["hubSiteId"] = dependsOnHub },
                    DependsOn = [id, .. HubStep(dependsOnHub)],
                    Source = HubDoc,
                });
            }

            return url;
        }

        // Home site
        var homeUrl = CreateSite(b.Home, "home", communication: true, dependsOnHub: null);
        var homeDepends = b.Home.ExistingUrl is null ? new List<string> { "home" } : [];
        steps.Add(new ImplementationStep
        {
            Id = "set-home-site",
            Phase = structure,
            Title = $"Make \"{b.Home.Title}\" the home site",
            Detail = "The organization's landing site; global navigation in the app bar and Viva Connections start from it.",
            Kind = StepKind.Automatic,
            Risk = StepRisk.Changes,
            Operation = StepOperation.SetHomeSite,
            Target = homeUrl,
            DependsOn = homeDepends,
            Source = PnpHomeSiteDoc,
            GuideUrl = $"{admin}/_layouts/15/online/AdminHome.aspx#/settings",
            Instructions = [$"SharePoint admin center → Settings → Home site → {homeUrl} → Save."],
            Rules = ["home.missing"],
        });
        steps.Add(Navigation("home-nav", structure, $"Build the navigation of \"{b.Home.Title}\" (used as global navigation)", homeUrl, b.GlobalNavigation, homeDepends));
        steps.Add(Guide("global-nav", structure, "Turn on global navigation in the app bar", homeUrl,
            [
                $"On {homeUrl}: Settings (gear) → Global navigation → Enable global navigation.",
                "Navigation source: Home site navigation (Hubwright built it in the step before).",
                "Title and logo: your organization's name and logo → Save.",
            ], StepRisk.Changes, ["home.global-nav-empty", "home.hubs-in-global-nav"], dependsOn: ["set-home-site", "home-nav"]));
        steps.Add(SuiteBarStep(b, structure));
        // Hubs: existing ones keep their id; new ones get it when they are registered.
        var hubRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hub in b.Hubs)
        {
            var hubSite = new BlueprintSite { Title = hub.Title, Slug = hub.Slug, Purpose = hub.Purpose, ExistingUrl = hub.ExistingUrl, Template = TemplateFor(proposal, hub.Title) };
            var url = CreateSite(hubSite, "hub-" + hub.Slug, communication: true, dependsOnHub: null);
            if (hub.ExistingUrl is not null && hubIdByUrl.TryGetValue(hub.ExistingUrl.TrimEnd('/'), out var existingId))
            {
                hubRef[hub.Slug] = existingId;
                var current = snapshot.Hubs.First(h => h.Id == existingId);
                if (!string.Equals(current.Title, hub.Title, StringComparison.Ordinal) && proposal.Answers.Approach != ProposalApproach.KeepWhatWorks)
                {
                    steps.Add(new ImplementationStep
                    {
                        Id = "rename-" + hub.Slug,
                        Phase = structure,
                        Title = $"Rename hub \"{current.Title}\" to \"{hub.Title}\"",
                        Detail = "The hub's display name in navigation and search; the site URL stays the same.",
                        Kind = StepKind.Automatic,
                        Risk = StepRisk.Changes,
                        Operation = StepOperation.RenameHub,
                        Target = url,
                        Parameters = { ["hubSiteId"] = existingId, ["title"] = hub.Title },
                        Source = HubDoc,
                        Rules = ["hubs.naming"],
                    });
                }
            }
            else if (hub.ExistingUrl is not null)
            {
                // A hub the scan saw without a usable id (an import or a sample): register it if needed at run time.
                hubRef[hub.Slug] = Register(hub, url, dependsOn: []);
            }
            else
            {
                hubRef[hub.Slug] = Register(hub, url, dependsOn: ["hub-" + hub.Slug]);
            }
        }

        string Register(BlueprintHub hub, string url, List<string> dependsOn)
        {
            var id = "register-" + hub.Slug;
            steps.Add(new ImplementationStep
            {
                Id = id,
                Phase = structure,
                Title = $"Register \"{hub.Title}\" as a hub",
                Detail = hub.Purpose,
                Kind = StepKind.Automatic,
                Risk = StepRisk.Adds,
                Operation = StepOperation.RegisterHub,
                Target = url,
                Parameters = { ["title"] = hub.Title },
                DependsOn = dependsOn,
                Source = HubDoc,
            });
            return "{" + id + ".hubSiteId}";
        }

        foreach (var hub in b.Hubs)
        {
            foreach (var site in hub.Sites)
            {
                CreateSite(site, "site-" + site.Slug, communication: site.Kind == SiteKind.Communication, dependsOnHub: site.ExistingUrl is null ? hubRef[hub.Slug] : null);
            }

            steps.Add(Navigation("nav-" + hub.Slug, structure, $"Build the navigation of {hub.Title}", siteUrlBySlug[hub.Slug], hub.Navigation, [.. HubStep(hubRef[hub.Slug]), .. hub.ExistingUrl is null && !HubStep(hubRef[hub.Slug]).Any() ? new[] { "hub-" + hub.Slug } : []]));
        }

        foreach (var site in b.Standalone)
        {
            CreateSite(site, "site-" + site.Slug, communication: site.Kind == SiteKind.Communication, dependsOnHub: null);
        }

        // Existing sites that move.
        foreach (var plan2 in proposal.Sites)
        {
            var slug = BlueprintGenerator.Slug(plan2.Title);
            if (plan2.Actions.Contains(SiteAction.AssociateWithHub) && plan2.TargetHub is { } target && b.Hubs.FirstOrDefault(h => h.Title == target) is { } hub)
            {
                steps.Add(new ImplementationStep
                {
                    Id = "move-" + slug,
                    Phase = structure,
                    Title = $"Associate \"{plan2.Title}\" with {hub.Title}",
                    Detail = string.Join(" ", plan2.Reasons.Take(2)),
                    Kind = StepKind.Automatic,
                    Risk = StepRisk.Changes,
                    Operation = StepOperation.AssociateWithHub,
                    Target = plan2.Url,
                    Parameters = { ["hubSiteId"] = hubRef[hub.Slug] },
                    DependsOn = [.. HubStep(hubRef[hub.Slug])],
                    Source = HubDoc,
                    Rules = [.. plan2.Rules.Where(r => r.StartsWith("hubs.", StringComparison.Ordinal) || r.StartsWith("ia.", StringComparison.Ordinal))],
                });
            }

            if (plan2.Actions.Contains(SiteAction.KeepOutOfHubs) && plan2.CurrentHub is not null)
            {
                steps.Add(new ImplementationStep
                {
                    Id = "detach-" + slug,
                    Phase = structure,
                    Title = $"Take extranet \"{plan2.Title}\" out of its hub",
                    Detail = "Guests stop seeing internal hub navigation. Add a link to it in hub navigation for internal users if they need it.",
                    Kind = StepKind.Automatic,
                    Risk = StepRisk.Changes,
                    Operation = StepOperation.DisassociateFromHub,
                    Target = plan2.Url,
                    Source = HubDoc,
                    Rules = ["hubs.extranet"],
                });
            }

            if (plan2.Actions.Contains(SiteAction.StopBeingHub))
            {
                steps.Add(Guide("unhub-" + slug, structure, $"Unregister \"{plan2.Title}\" as a hub", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement",
                    [
                        "Active sites → select the site → Hub → Unregister as hub site. Sites associated with it lose its navigation.",
                        plan2.TargetHub is { } t ? $"Then associate it with {t} (Hub → Change hub association)." : "Then decide where it belongs.",
                    ], StepRisk.Caution, ["hubs.depth"]));
            }
        }


        AddDesigns(plan, proposal, structure);

        // 3 · Modernize and tidy: never automatic.
        const string tidy = "3 · Modernize and tidy";
        void Many(string id, SiteAction action, string title, string page, string how, StepRisk risk, string[] rules)
        {
            var sites = proposal.Sites.Where(s => s.Actions.Contains(action)).ToList();
            if (sites.Count > 0)
            {
                steps.Add(Guide(id, tidy, $"{title} ({sites.Count})", page, [how, .. sites.Take(30).Select(s => $"{s.Title}: {s.Url}")], risk, rules));
            }
        }

        Many("modernize", SiteAction.Modernize, "Modernize classic sites", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement", "Create a modern site for each, move the content that still matters, then retire the classic one. The SharePoint Modernization scanner and page transformation help.", StepRisk.Changes, ["modern.classic-sites"]);
        Many("subsites", SiteAction.FlattenSubsites, "Turn subsites into sites", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement", "For each subsite that still matters: create a site, move its content, associate it with the hub; then delete the subsite.", StepRisk.Caution, ["ia.subsites"]);
        Many("archive", SiteAction.Archive, "Archive long-inactive sites after owners confirm", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement", "Ask each owner first. Active sites → select → Archive (Microsoft 365 Archive must be on). Hubwright never archives or deletes a site.", StepRisk.Caution, ["lifecycle.inactive-site"]);
        Many("retire", SiteAction.Retire, "Retire sites after their content has moved", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteManagement", "Only once the new home site and hubs hold the content: make it read-only, then archive or delete.", StepRisk.Caution, []);

        // 4 · Govern
        const string govern = "4 · Launch and govern";
        steps.Add(Guide("policies", govern, "Turn on lifecycle policies", $"{admin}/_layouts/15/online/AdminHome.aspx#/siteLifecycleManagement",
            [$"Site lifecycle management → Inactive site policy: {b.Policy.InactiveMonths} months, notify owners.", "Site ownership policy: at least " + b.Policy.MinimumOwners + " owners.", "Site attestation policy: owners confirm their sites yearly."],
            StepRisk.Changes, ["lifecycle.all-three"]));
        var unlabeled = proposal.Sites.Count(s => s.Actions.Contains(SiteAction.AddSensitivityLabel));
        if (unlabeled > 0)
        {
            steps.Add(Guide("labels", govern, $"Label {unlabeled} site(s)", "https://purview.microsoft.com/informationprotection",
                ["Publish container labels General, Confidential and Partner (Microsoft Purview → Information protection).", "Then set each site's label (Active sites → select → Settings → Sensitivity)."],
                StepRisk.Changes, ["copilot.unlabeled"]));
        }

        AddLaunch(plan, proposal, b, admin, homeUrl, homeDepends, govern);
        steps.Add(Guide("rescan", govern, "Scan again and compare", root, ["Setup → Scan now, then History → compare the two snapshots and check drift."], StepRisk.Adds, []));
        AssignHints(plan, b);
        return plan;
    }

    /// <summary>Navigation links, added with PnP PowerShell; links already there are kept.</summary>
    private static ImplementationStep Navigation(string id, string phase, string title, string url, IReadOnlyList<NavNode> nodes, IEnumerable<string> dependsOn) => new()
    {
        Id = id,
        Phase = phase,
        Title = title,
        Detail = "Adds the planned links to the site's top navigation (labels, then their links). Links already there are kept; nothing is removed. Audience targeting for private sites is set by hand.",
        Kind = StepKind.Automatic,
        Risk = StepRisk.Changes,
        Operation = StepOperation.BuildNavigation,
        Target = url,
        Parameters = { ["nav"] = JsonSerializer.Serialize(nodes, TenantSnapshot.Json) },
        DependsOn = [.. dependsOn],
        Source = PnpNavigationDoc,
        GuideUrl = url,
        Instructions = ["On the site: Edit (next to the navigation) → add these links:", .. Flatten(nodes).Select(n => "  " + n), "Links to private sites: turn on audience targeting so only members see them."],
        Rules = ["hubs.nav-sites", "nav.empty-labels", "hubs.private-links"],
    };
}
