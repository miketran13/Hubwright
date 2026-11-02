using System.Globalization;
using Hubwright.Blueprints;
using Hubwright.Rules;
using Hubwright.Scoring;
using Hubwright.Snapshot;
using Hubwright.Templates;
using Hubwright.Theming;

namespace Hubwright.Proposals;

/// <summary>The questionnaire's starting answers, read from the scan, with a note for each guess.</summary>
public sealed record ProposalSeed(ProposalAnswers Answers, IReadOnlyList<string> Observations);

/// <summary>
/// Builds an intranet proposal for a scanned tenant, deterministically: the target blueprint from the
/// answers, reusing the hubs and sites that already fit; a plan for every existing site; new sites
/// with the best-fitting Microsoft template or look book package; and a phased roadmap built from the
/// findings. The engine checks the target against the rules afterwards (<see cref="IntranetProposal.ScoreAfter"/>).
/// </summary>
public static class IntranetProposalGenerator
{
    // A guess the admin confirms in the questionnaire: hub or site names that read as places, not functions.
    private static readonly string[] RegionWords =
    [
        "emea", "apac", "americas", "amer", "latam", "nordics", "europe", "asia", "africa", "pacific", "oceania",
        "north america", "south america", "middle east", "uk", "usa", "canada", "germany", "france", "spain", "italy",
        "india", "china", "japan", "australia", "brazil", "mexico", "benelux", "dach", "iberia", "region", "office",
    ];

    private static readonly string[] Decorations = ["hub", "portal", "home", "site", "intranet", "team", "the"];

    // Which rules each site action answers, for the roadmap's citations (prefixes of rule ids).
    private static readonly Dictionary<SiteAction, string[]> ActionRules = new()
    {
        [SiteAction.AddOwners] = ["perm.ownerless", "perm.single-owner"],
        [SiteAction.StopBeingHub] = ["hubs."],
        [SiteAction.AssociateWithHub] = ["ia.orphan", "hubs."],
        [SiteAction.KeepOutOfHubs] = ["hubs.extranet"],
        [SiteAction.Modernize] = ["modern.", "brand.classic"],
        [SiteAction.FlattenSubsites] = ["ia.subsites"],
        [SiteAction.Archive] = ["lifecycle."],
        [SiteAction.ReviewWithOwner] = ["lifecycle."],
        [SiteAction.AddSensitivityLabel] = ["copilot.unlabeled"],
        [SiteAction.ReviewPermissions] = ["perm.", "sharing."],
    };

    public static string Human(SharingLevel level) => level switch
    {
        SharingLevel.Anyone => "Anyone (including unauthenticated links)",
        SharingLevel.NewAndExistingGuests => "new and existing guests",
        SharingLevel.ExistingGuests => "existing guests only",
        _ => "only people in the organization",
    };

    private static readonly string[] RiskSkills = ["permissions", "external-sharing", "copilot-readiness"];
    private static readonly string[] StructureSkills = ["information-architecture", "hub-sites", "navigation", "home-site-viva", "branding-theme", "search"];
    private static readonly string[] CleanupSkills = ["modernization", "site-lifecycle", "content-health", "page-performance"];

    public static ProposalSeed Seed(TenantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var notes = new List<string>();
        var org = Organization(snapshot);
        notes.Add($"Organization \"{org}\": from {(snapshot.HomeSite is not null ? "the home site's title" : snapshot.Hubs.Count > 0 ? "the top hub's title" : "the tenant name")}.");

        var departments = new List<string>();
        var regions = new List<string>();
        foreach (var hub in snapshot.Hubs)
        {
            var name = Clean(hub.Title, org);
            if (name.Length == 0)
            {
                continue;
            }

            (IsRegion(name) ? regions : departments).Add(Capital(name));
        }

        foreach (var site in snapshot.Sites.Where(s => !s.IsHub && IsRegion(Clean(s.Title, org))))
        {
            regions.Add(Capital(Clean(site.Title, org)));
        }

        departments = [.. departments.Distinct(StringComparer.OrdinalIgnoreCase)];

        // "EMEA sales" is the EMEA place of the Sales function: keep the place.
        regions = [.. regions
            .Select(r => string.Join(' ', r.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !departments.Contains(w, StringComparer.OrdinalIgnoreCase))))
            .Where(r => r.Length > 0)
            .Select(Capital)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        notes.Add(departments.Count > 0
            ? $"Functions from your hubs: {string.Join(", ", departments)}."
            : "No hubs name a function yet.");
        if (departments.Count < 3)
        {
            var suggested = new[] { "HR", "IT", "Finance", "Communications" }.Where(d => !departments.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
            departments.AddRange(suggested);
            notes.Add($"Added {string.Join(", ", suggested)} as suggestions: edit the list to match your organization.");
        }

        if (regions.Count > 0)
        {
            notes.Add($"Places from hub and site names: {string.Join(", ", regions)}. They become a Locations hub instead of a chain of hubs.");
        }

        var primary = snapshot.Themes.Select(t => t.Palette.GetValueOrDefault("themePrimary")).FirstOrDefault(p => p is not null && Rgb.TryParse(p, out _));
        if (primary is not null)
        {
            notes.Add($"Brand colour {primary}: from your tenant theme \"{snapshot.Themes.First(t => t.Palette.GetValueOrDefault("themePrimary") == primary).Name}\".");
        }

        var sharing = snapshot.Settings.SharingLevel;
        if (sharing is not null)
        {
            notes.Add($"External sharing is {sharing} today; the questions below decide the target.");
        }

        var employees = snapshot.Settings.LicensedUsers ?? Math.Max(50, snapshot.Sites.Count * 20);
        notes.Add(snapshot.Settings.LicensedUsers is null ? $"Employees: {employees} is a guess from the site count; correct it." : $"Employees: {employees} licensed users.");

        var answers = new ProposalAnswers
        {
            Intranet = new IntranetAnswers
            {
                Organization = org,
                TenantPrefix = string.IsNullOrWhiteSpace(snapshot.Tenant) ? "contoso" : BlueprintGenerator.Slug(snapshot.Tenant),
                Employees = employees,
                Departments = departments,
                Regions = regions,
                Goals = ["news", "policies", "onboarding", .. snapshot.Sites.Any(s => s.Kind == SiteKind.TeamGroup) ? new[] { "collaboration" } : []],
                ExternalCollaboration = snapshot.Sites.Any(s => s.IsExtranet) || sharing is SharingLevel.NewAndExistingGuests or SharingLevel.Anyone,
                AllowAnyoneLinks = false,
                BrandColor = primary ?? "#0f6cbd",
                HeaderLayout = "standard",
            },
        };
        if (snapshot.Sites.Any(s => s.IsExtranet))
        {
            notes.Add("You have extranet sites, so partner collaboration is on.");
        }

        return new ProposalSeed(answers, notes);
    }

    public static IntranetProposal Generate(
        TenantSnapshot snapshot,
        Assessment current,
        ProposalAnswers answers,
        IReadOnlyList<SiteTemplate> templates,
        IReadOnlyList<ProposalDecision> decisions,
        IReadOnlyList<string>? observations = null,
        IReadOnlyList<Designs.SitePattern>? patterns = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(templates);
        var blueprint = BlueprintGenerator.Generate(answers.Intranet);
        blueprint.Name = $"{answers.Intranet.Organization} intranet proposal";
        var proposal = new IntranetProposal
        {
            Name = $"{answers.Intranet.Organization} intranet proposal {Values.Now:yyyy-MM-dd}",
            Tenant = snapshot.Tenant,
            SnapshotTaken = snapshot.CapturedAt,
            Answers = answers,
            Observations = [.. observations ?? []],
            ScoreBefore = current.Overall,
            SiteCount = snapshot.Sites.Count,
            HubCount = snapshot.Hubs.Count,
            FindingCount = current.Findings.Count,
            NotAssessed = current.Results.Count(r => r.Status == RuleStatus.NotAssessed),
            UnreadSites = snapshot.Sites.Count(s => s.Readable == false),
            Blueprint = blueprint,
            Decisions = [.. decisions],
        };

        proposal.TopIssues = [.. current.Findings.GroupBy(f => f.RuleId)
            .Select(g => (First: g.First(), Count: g.Count()))
            .OrderByDescending(x => x.First.Severity).ThenByDescending(x => x.Count)
            .Take(12)
            .Select(x => new ProposalIssue { RuleId = x.First.RuleId, Title = x.First.Title, SkillId = x.First.SkillId, Severity = x.First.Severity, Basis = x.First.Basis, Count = x.Count, Fix = x.First.Fix, Source = x.First.Source, Quote = x.First.Quote })];

        var byTarget = current.Findings.ToLookup(f => f.Target, StringComparer.OrdinalIgnoreCase);
        Reuse(snapshot, blueprint, answers, proposal);
        PlanSites(snapshot, blueprint, answers, byTarget, proposal);
        PlanNewSites(blueprint, templates, answers, proposal);
        ThemeNotes(snapshot, answers, blueprint, proposal);
        proposal.Designs = [.. Designs.SiteDesigner.Design(proposal, snapshot, patterns ?? [], templates)];
        proposal.Phases = Phases(current, proposal, blueprint);
        proposal.Citations = Citations(current, templates, proposal);
        return proposal;
    }

    /// <summary>Points target hubs, sites and the home site at what already exists, so nothing is built twice.</summary>
    private static void Reuse(TenantSnapshot snapshot, Blueprint blueprint, ProposalAnswers answers, IntranetProposal proposal)
    {
        var org = answers.Intranet.Organization;
        var home = snapshot.HomeSite;
        if (home is not null && home.Kind == SiteKind.Communication)
        {
            blueprint.Home.ExistingUrl = home.Url;
            blueprint.Home.Title = home.Title;
            blueprint.Rationale.Add($"Your home site \"{home.Title}\" stays the front door.");
        }

        foreach (var hub in blueprint.Hubs)
        {
            var name = hub.Title.Replace(" Hub", "", StringComparison.Ordinal);
            var existing = snapshot.Hubs.FirstOrDefault(h => Matches(Clean(h.Title, org), name));
            if (existing is not null && string.IsNullOrWhiteSpace(existing.SiteUrl))
            {
                blueprint.Rationale.Add($"A hub named \"{existing.Title}\" exists, but the scan could not see its site (you may not be able to open it, or it was deleted). Check it with someone who can before the build creates {hub.Title}, so there aren't two.");
            }
            else if (existing is not null)
            {
                hub.ExistingUrl = existing.SiteUrl;
                blueprint.Rationale.Add($"\"{existing.Title}\" becomes {hub.Title}: the hub already exists, so it is kept{(existing.Title != hub.Title ? " and renamed to the naming convention" : "")}.");
            }

            foreach (var site in hub.Sites)
            {
                var match = snapshot.Sites.FirstOrDefault(s => !s.IsHub && !s.IsHomeSite && (Matches(Clean(s.Title, org), site.Title) || Matches(Clean(s.Title, org), Short(site.Title))));
                if (match is not null)
                {
                    site.ExistingUrl = match.Url;
                }
            }
        }

        // Places that are hubs today become sites in the Locations hub (fewer hub levels).
        var locations = blueprint.Hubs.FirstOrDefault(h => h.Slug == "locations-hub");
        if (locations is not null)
        {
            foreach (var site in locations.Sites)
            {
                var region = site.Title.Replace(" Office", "", StringComparison.Ordinal);
                var match = snapshot.Sites.FirstOrDefault(s => !s.IsHomeSite && Matches(Clean(s.Title, org), region));
                if (match is not null)
                {
                    site.ExistingUrl = match.Url;
                }
            }
        }

        _ = proposal;
    }

    private static void PlanSites(TenantSnapshot snapshot, Blueprint blueprint, ProposalAnswers answers, ILookup<string, Finding> findings, IntranetProposal proposal)
    {
        var org = answers.Intranet.Organization;
        var archiveAfter = answers.Approach switch
        {
            ProposalApproach.Restructure => blueprint.Policy.InactiveMonths * 30,
            ProposalApproach.Balanced => blueprint.Policy.InactiveMonths * 60,
            _ => int.MaxValue,
        };
        var reviewAfter = blueprint.Policy.InactiveMonths * 30;
        var hubTitle = snapshot.Hubs.Where(h => h.Id.Length > 0).GroupBy(h => h.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var targetHubByUrl = blueprint.Hubs.Where(h => !string.IsNullOrWhiteSpace(h.ExistingUrl)).GroupBy(h => h.ExistingUrl!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var targetSiteByUrl = blueprint.Hubs.SelectMany(h => h.Sites.Select(s => (Hub: h, Site: s))).Where(x => !string.IsNullOrWhiteSpace(x.Site.ExistingUrl))
            .GroupBy(x => x.Site.ExistingUrl!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var site in snapshot.Sites)
        {
            var plan = new SitePlan
            {
                Url = site.Url,
                Title = site.Title,
                Kind = site.Kind,
                IsHub = site.IsHub,
                CurrentHub = site.HubId is { } id ? hubTitle.GetValueOrDefault(id) : null,
                DaysInactive = site.LastActivity is { } last ? (int)Math.Floor((Values.Now - last).TotalDays) : null,
            };
            var rules = findings[site.Url].Select(f => f.RuleId).Distinct(StringComparer.Ordinal).ToList();
            plan.Rules.AddRange(rules);
            var hubUnread = snapshot.Unread(ScanAreas.Hubs, site.Url);
            var homeCandidate = snapshot.HomeSite is null && site.IsHub && site.Title.Contains(org, StringComparison.OrdinalIgnoreCase) && site.HubId is not null && snapshot.Hubs.FirstOrDefault(h => h.Id == site.HubId)?.ParentHubId is null;

            if (site.IsHomeSite)
            {
                plan.TargetHub = null;
                plan.Disposition = site.Kind == SiteKind.Communication ? "keep" : "modernize";
                plan.Actions.Add(SiteAction.Keep);
                plan.Reasons.Add("The home site: it carries company news and the global navigation.");
            }
            else if (targetHubByUrl.TryGetValue(site.Url, out var hub))
            {
                plan.TargetHub = hub.Title;
                plan.Actions.Add(SiteAction.Keep);
                if (!string.Equals(site.Title, hub.Title, StringComparison.Ordinal) && answers.Approach != ProposalApproach.KeepWhatWorks)
                {
                    plan.Actions.Add(SiteAction.RenameHub);
                    plan.Reasons.Add($"Rename to \"{hub.Title}\" so every hub follows one naming convention.");
                }

                if (hub.Parent is null && site.HubId is { } hid && snapshot.Hubs.FirstOrDefault(h => h.Id == hid)?.ParentHubId is not null && answers.Approach != ProposalApproach.KeepWhatWorks)
                {
                    plan.Reasons.Add("Detach it from its parent hub: the target keeps hub-to-hub association shallow.");
                }

                plan.Disposition = "keep";
            }
            else if (site.IsHub && homeCandidate)
            {
                plan.Disposition = answers.Approach == ProposalApproach.KeepWhatWorks ? "modernize" : "retire";
                plan.Actions.Add(answers.Approach == ProposalApproach.KeepWhatWorks ? SiteAction.Modernize : SiteAction.Retire);
                plan.Reasons.Add(site.Kind == SiteKind.Classic
                    ? $"Today's classic intranet root. Its content moves to the new modern home site \"{blueprint.Home.Title}\" and the hubs; then it is retired."
                    : $"Today's top hub. The new home site \"{blueprint.Home.Title}\" with the app bar takes over as the front door.");
            }
            else if (targetSiteByUrl.TryGetValue(site.Url, out var placed))
            {
                plan.TargetHub = placed.Hub.Title;
                plan.Disposition = "move";
                if (site.IsHub && answers.Approach != ProposalApproach.KeepWhatWorks)
                {
                    plan.Actions.Add(SiteAction.StopBeingHub);
                    plan.Reasons.Add($"A place, not a function: it becomes \"{placed.Site.Title}\" in {placed.Hub.Title}, which removes a level of hub nesting.");
                }

                if (!string.Equals(plan.CurrentHub, placed.Hub.Title, StringComparison.Ordinal))
                {
                    plan.Actions.Add(SiteAction.AssociateWithHub);
                    plan.Reasons.Add($"Associate with {placed.Hub.Title}.");
                }
                else
                {
                    plan.Disposition = "keep";
                }
            }
            else if (site.IsExtranet)
            {
                plan.Disposition = site.HubId is null ? "keep" : "move";
                plan.Actions.Add(SiteAction.KeepOutOfHubs);
                plan.Reasons.Add(site.HubId is null ? "Extranet: stays out of every hub." : "Extranet: disassociate it from its hub so guests never see internal navigation.");
            }
            else
            {
                var target = TargetFor(site, blueprint, org);
                if (target is not null)
                {
                    plan.TargetHub = target.Title;
                    plan.Disposition = plan.CurrentHub == target.Title ? "keep" : "move";
                    if (plan.CurrentHub != target.Title)
                    {
                        plan.Actions.Add(SiteAction.AssociateWithHub);
                        plan.Reasons.Add($"Its name says {target.Title.Replace(" Hub", "", StringComparison.Ordinal)}: associate it with {target.Title}{(hubUnread ? " (its current hub could not be read; the build checks before it changes anything)" : "")}.");
                    }
                }
                else if (site.IsHub)
                {
                    plan.Disposition = "review";
                    plan.Actions.Add(answers.Approach == ProposalApproach.KeepWhatWorks ? SiteAction.Keep : SiteAction.StopBeingHub);
                    plan.Reasons.Add("A hub with no place in the target structure: keep it only if it serves a function with several services.");
                }
                else if (site.Kind is SiteKind.TeamGroup or SiteKind.TeamNoGroup or SiteKind.Channel)
                {
                    plan.Disposition = "keep";
                    plan.Actions.Add(SiteAction.Keep);
                    plan.Reasons.Add(site.HubId is null
                        ? "A team workspace: it can stay outside the hubs, or join the hub of the function that owns it."
                        : "A team workspace in a hub: keep it out of the hub's navigation, or show it only to its members (audience targeting).");
                }
                else
                {
                    plan.Disposition = "review";
                    plan.Actions.Add(SiteAction.ChooseHub);
                    plan.Reasons.Add("No hub fits its name: the owner chooses the function it belongs to.");
                }
            }

            // Health actions apply whatever the disposition.
            if (site.Kind == SiteKind.Classic && plan.Disposition != "retire")
            {
                plan.Actions.Add(SiteAction.Modernize);
                plan.Reasons.Add("Classic site: move to a modern communication or team site (modern pages, themes and hubs).");
                plan.Disposition = plan.Disposition == "keep" ? "modernize" : plan.Disposition;
            }

            if (site.SubsiteCount > 0)
            {
                plan.Actions.Add(SiteAction.FlattenSubsites);
                plan.Reasons.Add($"{site.SubsiteCount} subsites: turn each into its own site and associate it with the hub instead.");
            }

            if (!site.IsHomeSite && plan.DaysInactive is { } days && plan.Disposition != "retire")
            {
                if (days > archiveAfter)
                {
                    plan.Actions.Add(SiteAction.Archive);
                    plan.Disposition = "archive";
                    plan.Reasons.Add($"No activity for {days} days: archive it (Microsoft 365 Archive) after the owner confirms.");
                }
                else if (days > reviewAfter)
                {
                    plan.Actions.Add(SiteAction.ReviewWithOwner);
                    plan.Reasons.Add($"No activity for {days} days: the inactive-site policy asks the owner to confirm it is still needed.");
                }
            }

            if (site.OwnerCount is { } owners && owners < blueprint.Policy.MinimumOwners && plan.Disposition is not ("archive" or "retire"))
            {
                plan.Actions.Add(SiteAction.AddOwners);
                plan.Reasons.Add(owners == 0 ? "No owner: add two." : $"Only {owners} owner: add another.");
            }

            if ((site.Permissions.EeeuGrants ?? 0) + (site.Permissions.EveryoneGrants ?? 0) > 0 || (site.Permissions.AnyoneLinks ?? 0) > 0)
            {
                plan.Actions.Add(SiteAction.ReviewPermissions);
                plan.Reasons.Add("Open to everyone (Everyone or Anyone links): review before Copilot surfaces its content.");
            }

            if (site.SensitivityLabel is null && snapshot.ScanIssues.All(i => i.Area != ScanAreas.Labels) && plan.Disposition is not ("archive" or "retire"))
            {
                plan.Actions.Add(SiteAction.AddSensitivityLabel);
                plan.Reasons.Add($"No sensitivity label: apply {(site.IsExtranet ? "Partner" : site.IsPublic == false ? "Confidential" : "General")}.");
            }

            // Unknown is not pass: a site nobody could read gets checked by someone who can.
            if (site.Readable == false)
            {
                plan.Actions.Add(SiteAction.CheckUnread);
                plan.Reasons.Add("Not readable with your access (you are not a member): only its activity and storage are known, from the usage report. Its owners, permissions and content were not assessed.");
            }

            // An archived site needs no new home: only the archive step, and closing what is open.
            if (plan.Disposition == "archive")
            {
                var keep = new[] { SiteAction.Archive, SiteAction.ReviewPermissions, SiteAction.CheckUnread };
                var reasons = plan.Reasons.Where(r => r.StartsWith("No activity", StringComparison.Ordinal) || r.StartsWith("Open to everyone", StringComparison.Ordinal) || r.StartsWith("Not readable", StringComparison.Ordinal)).ToList();
                plan.Actions = [.. plan.Actions.Where(keep.Contains)];
                plan.Reasons = reasons.Count > 0 ? reasons : plan.Reasons;
            }

            proposal.Sites.Add(plan);
        }
    }

    private static BlueprintHub? TargetFor(SiteInfo site, Blueprint blueprint, string org)
    {
        var text = $"{Clean(site.Title, org)} {site.Url}";
        return blueprint.Hubs.FirstOrDefault(h => h.Slug != "locations-hub" && Words(text).Contains(h.Title.Replace(" Hub", "", StringComparison.Ordinal).ToLowerInvariant()));
    }

    private static void PlanNewSites(Blueprint blueprint, IReadOnlyList<SiteTemplate> templates, ProposalAnswers answers, IntranetProposal proposal)
    {
        void Add(BlueprintSite site, string? hub, bool isHub = false)
        {
            var kind = isHub ? SiteKind.Communication : site.Kind;
            var purpose = site.Purpose + (site == blueprint.Home ? " home front door company news intranet" : "");
            var matches = TemplateMatcher.Suggest(templates, site.Title, purpose, kind, answers.UsePackages);
            var best = matches.FirstOrDefault()?.Template ?? TemplateMatcher.Default(templates, kind);
            site.Template = best?.Id;
            if (site.ExistingUrl is not null)
            {
                return;
            }

            proposal.NewSites.Add(new NewSitePlan
            {
                Title = site.Title,
                Url = blueprint.Url(site.Slug),
                Hub = hub,
                Purpose = site.Purpose,
                Kind = kind,
                TemplateId = best?.Id,
                TemplateName = best?.Name,
                HowToApply = best?.HowToApply,
                Alternatives = [.. matches.Skip(1).Select(m => m.Template.Name)],
            });
        }

        Add(blueprint.Home, null);
        foreach (var hub in blueprint.Hubs)
        {
            var hubSite = new BlueprintSite { Title = hub.Title, Slug = hub.Slug, Purpose = hub.Purpose, ExistingUrl = hub.ExistingUrl };
            Add(hubSite, null, isHub: true);
            foreach (var site in hub.Sites)
            {
                Add(site, hub.Title);
            }
        }

        foreach (var site in blueprint.Standalone)
        {
            Add(site, null);
        }
    }

    private static void ThemeNotes(TenantSnapshot snapshot, ProposalAnswers answers, Blueprint blueprint, IntranetProposal proposal)
    {
        var wanted = Rgb.Parse(answers.Intranet.BrandColor);
        var theme = blueprint.Theme();
        proposal.ThemeNotes.Add(wanted.Hex.Equals(blueprint.BrandColor, StringComparison.OrdinalIgnoreCase)
            ? $"Brand colour {wanted.Hex} passes WCAG 2.1 AA as text and behind white button labels."
            : $"Brand colour {wanted.Hex} fails WCAG 2.1 AA contrast; the theme uses {blueprint.BrandColor}, the nearest lightness that passes. Keep {wanted.Hex} for large graphics and logos.");
        proposal.ThemeNotes.Add($"{theme.Checks.Count(c => c.Passes)} of {theme.Checks.Count} text and background pairs pass.");
        if (snapshot.Settings.HideDefaultThemes != true)
        {
            proposal.ThemeNotes.Add("Hide SharePoint's default themes so sites choose from the organization's themes only (theme.ps1 does this).");
        }

        var classic = snapshot.Sites.Count(s => s.UsesClassicTheme == true);
        if (classic > 0)
        {
            proposal.ThemeNotes.Add($"{classic} site(s) use classic themes; modern themes only apply to modern pages.");
        }

        proposal.ThemeNotes.Add($"Header layout: {blueprint.HeaderLayout}. Hub navigation: mega menu where a hub has three levels.");
    }

    private static List<ProposalPhase> Phases(Assessment current, IntranetProposal proposal, Blueprint blueprint)
    {
        static ProposalItem Item(string text, IEnumerable<string>? rules = null) => new() { Text = text, Rules = [.. rules ?? []] };
        IEnumerable<ProposalItem> FindingItems(IEnumerable<string> skills) => current.Findings
            .Where(f => skills.Contains(f.SkillId, StringComparer.OrdinalIgnoreCase))
            .GroupBy(f => f.RuleId)
            .OrderByDescending(g => g.First().Severity).ThenByDescending(g => g.Count())
            .Select(g => Item($"{g.First().Title}{(g.Count() > 1 ? $" ({g.Count()})" : "")}{(g.First().Fix is { } fix ? ": " + fix : "")}", [g.Key]));
        int Count(SiteAction action) => proposal.Sites.Count(s => s.Actions.Contains(action));
        List<string> RulesOf(SiteAction action)
        {
            var prefixes = ActionRules.GetValueOrDefault(action) ?? [];
            return [.. proposal.Sites.Where(s => s.Actions.Contains(action)).SelectMany(s => s.Rules)
                .Where(r => prefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal))).Distinct(StringComparer.Ordinal).Take(6)];
        }

        var risks = new ProposalPhase { Name = "1 · Close the risks", When = "Weeks 1–3", Goal = "Nobody sees what they shouldn't, before the new intranet makes content easier to find." };
        risks.Items.AddRange(FindingItems(RiskSkills));
        if (Count(SiteAction.CheckUnread) > 0)
        {
            risks.Items.Add(Item($"Check {Count(SiteAction.CheckUnread)} site(s) the scan could not read (you are not a member): their owners, permissions and content were not assessed."));
        }

        risks.Items.Add(Item($"Set sharing to {Human(blueprint.Policy.Sharing)}, default links to people in the organization{(blueprint.Policy.Sharing == SharingLevel.Anyone ? $", Anyone links view-only and expiring after {blueprint.Policy.AnyoneLinkExpirationDays} days" : "")}."));

        var structure = new ProposalPhase { Name = "2 · Build the structure", When = "Weeks 3–8", Goal = "One home site, one hub per function, every site where people expect it." };
        if (blueprint.Home.ExistingUrl is null)
        {
            structure.Items.Add(Item($"Create the home site \"{blueprint.Home.Title}\" ({proposal.NewSites.FirstOrDefault(n => n.Title == blueprint.Home.Title)?.TemplateName ?? "communication site"}), set it as the home site and turn on the app bar's global navigation."));
        }

        var newHubs = blueprint.Hubs.Where(h => h.ExistingUrl is null).Select(h => h.Title).ToList();
        if (newHubs.Count > 0)
        {
            structure.Items.Add(Item($"Create and register {newHubs.Count} hub(s): {string.Join(", ", newHubs)}."));
        }

        if (Count(SiteAction.RenameHub) > 0)
        {
            structure.Items.Add(Item($"Rename {Count(SiteAction.RenameHub)} hub(s) to the naming convention ({blueprint.Policy.NamingConvention})."));
        }

        if (Count(SiteAction.StopBeingHub) > 0)
        {
            structure.Items.Add(Item($"Unregister {Count(SiteAction.StopBeingHub)} hub(s) that are places or have no function, and associate them as sites.", RulesOf(SiteAction.StopBeingHub)));
        }

        if (Count(SiteAction.AssociateWithHub) > 0)
        {
            structure.Items.Add(Item($"Associate {Count(SiteAction.AssociateWithHub)} existing site(s) with their target hub.", RulesOf(SiteAction.AssociateWithHub)));
        }

        var newSites = proposal.NewSites.Count(n => n.Title != blueprint.Home.Title && !newHubs.Contains(n.Title));
        if (newSites > 0)
        {
            structure.Items.Add(Item($"Create {newSites} new site(s) from ready-made templates (see New sites)."));
        }

        var designed = proposal.Designs.Count(d => d.Apply);
        if (designed > 0)
        {
            structure.Items.Add(Item($"Draft the designed home page on {designed} site(s) for their owners to review and publish (see Site designs)."));
        }

        structure.Items.Add(Item("Build hub navigation (three levels at most, mega menu), and the global navigation in the app bar."));
        structure.Items.Add(Item($"Apply the {blueprint.Organization} theme and hide the default themes (theme.ps1)."));
        structure.Items.AddRange(FindingItems(StructureSkills));

        var cleanup = new ProposalPhase { Name = "3 · Modernize and tidy", When = "Weeks 6–12", Goal = "No classic pages, no subsites, nothing abandoned." };
        if (Count(SiteAction.Modernize) > 0)
        {
            cleanup.Items.Add(Item($"Modernize {Count(SiteAction.Modernize)} classic site(s).", RulesOf(SiteAction.Modernize)));
        }

        if (Count(SiteAction.FlattenSubsites) > 0)
        {
            cleanup.Items.Add(Item($"Flatten {current.Snapshot.Sites.Where(s => proposal.Sites.Any(p => p.Url == s.Url && p.Actions.Contains(SiteAction.FlattenSubsites))).Sum(s => s.SubsiteCount ?? 0)} subsites in {Count(SiteAction.FlattenSubsites)} site(s) into sites of their own.", RulesOf(SiteAction.FlattenSubsites)));
        }

        if (Count(SiteAction.Retire) > 0)
        {
            cleanup.Items.Add(Item($"Retire {Count(SiteAction.Retire)} site(s) once their content has moved."));
        }

        if (Count(SiteAction.Archive) > 0)
        {
            cleanup.Items.Add(Item($"Archive {Count(SiteAction.Archive)} long-inactive site(s) after their owners confirm.", RulesOf(SiteAction.Archive)));
        }

        if (Count(SiteAction.ReviewWithOwner) > 0)
        {
            cleanup.Items.Add(Item($"Ask the owners of {Count(SiteAction.ReviewWithOwner)} quiet site(s) whether they are still needed."));
        }

        cleanup.Items.AddRange(FindingItems(CleanupSkills));

        var govern = new ProposalPhase { Name = "4 · Launch and govern", When = "Week 12 onwards", Goal = "Keep it healthy: policies on, owners trained, a scan every month." };
        govern.Items.Add(Item($"Turn on the inactive-site ({blueprint.Policy.InactiveMonths} months), site ownership and attestation policies together."));
        if (Count(SiteAction.AddSensitivityLabel) > 0)
        {
            govern.Items.Add(Item($"Label {Count(SiteAction.AddSensitivityLabel)} site(s): General, Confidential for private team sites, Partner for extranets.", RulesOf(SiteAction.AddSensitivityLabel)));
        }

        foreach (var decision in proposal.Decisions.Where(d => !string.IsNullOrWhiteSpace(d.Answer)))
        {
            govern.Items.Add(Item($"{decision.Question} → {decision.Answer}"));
        }

        govern.Items.AddRange(FindingItems(["governance"]));
        govern.Items.Add(Item("Scan again every month with Hubwright and compare snapshots (History)."));
        foreach (var item in new[] { risks, structure, cleanup, govern }.SelectMany(p => p.Items).Where(i => i.Rules.Count == 0 || i.Hint is null))
        {
            item.Hint ??= HintFor(item.Text);
        }

        return [risks, structure, cleanup, govern];
    }

    /// <summary>The hint for one of the generator's own roadmap lines, by how it starts.</summary>
    private static string? HintFor(string text)
    {
        (string Prefix, string Hint)[] map =
        [
            ("Set sharing", "sharing-level"), ("Create the home site", "home-site"), ("Create and register", "register-hub"),
            ("Rename", "rename-hub"), ("Unregister", "unregister-hub"), ("Associate", "associate-with-hub"),
            ("Create ", "create-site"), ("Build hub navigation", "hub-navigation"), ("Apply the", "theme"),
            ("Modernize", "modernize"), ("Flatten", "flatten-subsites"), ("Retire", "retire-site"), ("Archive", "archive-site"),
            ("Ask the owners", "review-inactive"), ("Turn on the inactive", "lifecycle-policies"), ("Label", "sensitivity-labels"),
            ("Scan again", "rescan"), ("Check ", "unread-site"), ("Draft the designed", "site-design"),
        ];
        return map.FirstOrDefault(m => text.StartsWith(m.Prefix, StringComparison.Ordinal)).Hint;
    }

    private static List<ProposalCitation> Citations(Assessment current, IReadOnlyList<SiteTemplate> templates, IntranetProposal proposal)
    {
        var citations = current.Findings.Where(f => f.Source is not null)
            .GroupBy(f => f.Source!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ProposalCitation
            {
                Url = g.Key,
                Quotes = [.. g.Select(f => f.Quote).OfType<string>().Distinct(StringComparer.Ordinal).Take(6)],
                Rules = [.. g.Select(f => f.RuleId).Distinct(StringComparer.Ordinal)],
            })
            .ToList();
        var used = proposal.NewSites.Select(n => n.TemplateId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in templates.Where(t => used.Contains(t.Id)).GroupBy(t => t.SourceUrl, StringComparer.OrdinalIgnoreCase))
        {
            citations.Add(new ProposalCitation { Url = group.Key, Quotes = [.. group.Select(t => t.Quote).OfType<string>().Take(6)], Rules = [.. group.Select(t => "template:" + t.Id)] });
        }

        return citations;
    }

    private static string Organization(TenantSnapshot snapshot)
    {
        var candidates = new[] { snapshot.HomeSite?.Title, snapshot.Hubs.FirstOrDefault(h => h.ParentHubId is null)?.Title };
        foreach (var title in candidates.OfType<string>())
        {
            var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Decorations.Contains(w.ToLowerInvariant())).ToList();
            if (words.Count is > 0 and <= 3)
            {
                return string.Join(' ', words);
            }
        }

        return string.IsNullOrWhiteSpace(snapshot.Tenant) ? "Contoso" : Capital(snapshot.Tenant);
    }

    /// <summary>A title without the organization's name and words like Hub or Portal: "Fabrikam Sales Portal" → "Sales".</summary>
    internal static string Clean(string title, string organization)
    {
        var words = title.Split([' ', '-', '–', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Decorations.Contains(w.ToLowerInvariant()) && !organization.Split(' ').Contains(w, StringComparer.OrdinalIgnoreCase));
        return string.Join(' ', words);
    }

    internal static bool IsRegion(string name)
    {
        var lower = name.ToLowerInvariant();
        return RegionWords.Any(r => r.Contains(' ', StringComparison.Ordinal) ? lower.Contains(r, StringComparison.Ordinal) : Words(lower).Contains(r));
    }

    private static bool Matches(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wa = Words(a);
        var wb = Words(b);
        return wb.Count > 0 && wb.All(wa.Contains) || wa.Count > 0 && wa.All(wb.Contains);
    }

    private static string Short(string title)
    {
        var dash = title.IndexOf(" – ", StringComparison.Ordinal);
        return dash > 0 ? title[(dash + 3)..] : title;
    }

    private static HashSet<string> Words(string text)
        => [.. text.ToLowerInvariant().Split([' ', '-', '–', '_', '/', '.', ',', '(', ')', ':'], StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1)];

    private static string Capital(string text)
        => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
}
