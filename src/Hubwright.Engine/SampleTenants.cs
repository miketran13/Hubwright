using Hubwright.Blueprints;
using Hubwright.Snapshot;
using Hubwright.Theming;

namespace Hubwright.Engine;

/// <summary>
/// Made-up tenants for trying Hubwright without signing in, for the desktop preview mode and for
/// tests. <see cref="Fabrikam"/> has grown for years without a plan; <see cref="Northwind"/> follows
/// the guidance. Both are fictional.
/// </summary>
public static class SampleTenants
{
    public static TenantSnapshot ByName(string name, DateTimeOffset? now = null) => name.ToLowerInvariant() switch
    {
        "northwind" or "healthy" => Northwind(now),
        _ => Fabrikam(now),
    };

    /// <summary>A messy intranet: subsites, one overloaded hub, permissive sharing, ownerless and stale sites.</summary>
    public static TenantSnapshot Fabrikam(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        const string root = "https://fabrikam.sharepoint.com/sites/";
        var snapshot = new TenantSnapshot
        {
            Tenant = "Fabrikam",
            CapturedAt = t,
            Source = SnapshotSource.Sample,
            Settings = new TenantSettings
            {
                SharingLevel = SharingLevel.Anyone,
                DefaultLinkType = DefaultLinkType.Anyone,
                DefaultLinkPermission = LinkAccess.Edit,
                AnyoneLinkExpirationDays = 0,
                AnyoneFileLinkPermission = LinkAccess.Edit,
                AnyoneFolderLinkPermission = LinkAccess.Edit,
                GuestAccessExpirationDays = 0,
                CustomScriptAllowed = true,
                HideDefaultThemes = false,
                SelfServiceSiteCreation = true,
                Microsoft365ArchiveEnabled = false,
                InactiveSitePolicies = 0,
                SiteOwnershipPolicies = 0,
                SiteAttestationPolicies = 0,
                BackupEnabled = false,
                DaysSinceContentAssessment = 200,
                LicensedUsers = 1800,
                StorageQuotaMb = 19_456_000,
                StorageUsedMb = 18_200_000,
                SearchBookmarks = 0,
                SearchAcronyms = 0,
            },
            Themes = [Theme("Fabrikam Yellow", "#f2c811")],
        };

        var intranet = new SiteInfo
        {
            Url = root + "intranet",
            Title = "Fabrikam Intranet",
            Template = "STS#0",
            Kind = SiteKind.Classic,
            IsHub = true,
            HubId = "hub-intranet",
            OwnerCount = 1,
            SubsiteCount = 37,
            IsPublic = true,
            LastActivity = t.AddDays(-3),
            UsesClassicTheme = true,
            MenuStyle = MenuStyle.Cascading,
            Navigation = [.. Enumerable.Range(1, 6).Select(i => new NavNode
            {
                Title = $"Section {i}",
                Children = [.. Enumerable.Range(1, 24).Select(j => new NavNode { Title = $"Page {i}.{j}", Url = $"{root}intranet/page{i}{j}.aspx", Children = j == 1 ? [new NavNode { Title = "More", Children = [new NavNode { Title = "Even more", Url = $"{root}intranet/deep{i}.aspx" }] }] : [] })],
            }), new NavNode { Title = "Forms" }],
            Permissions = new PermissionStats { UniqueScopes = 640, MaxUniqueScopesInList = 6200, EveryoneGrants = 1, EeeuGrants = 3, AnyoneLinks = 48, GuestCount = 12 },
            Content = new ContentStats { Pages = 900, ClassicPages = 650, Lists = 120, Libraries = 64, MaxFolderDepth = 7, LargestListItems = 140_000, LibrariesWithoutVersioning = 9, HomePageWebParts = 31, HomePageImagesOver300Kb = 6, MaxPathLength = 382, Files = 410_000, UsesClassicPublishing = true, UsesInfoPath = true, UsesClassicWorkflows = true, LastNewsPublished = t.AddDays(-140) },
        };
        snapshot.Sites.Add(intranet);
        snapshot.Hubs.Add(new HubInfo
        {
            Id = "hub-intranet",
            Title = "Fabrikam Intranet",
            SiteUrl = intranet.Url,
            ThemeName = "Fabrikam Yellow",
            Navigation = [.. Enumerable.Range(1, 140).Select(i => new NavNode { Title = $"Team {i}", Url = $"{root}team{i}", Restricted = i % 10 == 0 })],
        });
        snapshot.Hubs.Add(new HubInfo { Id = "hub-sales", Title = "Sales Portal", SiteUrl = root + "sales", ParentHubId = "hub-intranet", ThemeName = "Blue" });
        snapshot.Hubs.Add(new HubInfo { Id = "hub-emea", Title = "EMEA sales", SiteUrl = root + "emea", ParentHubId = "hub-sales", ThemeName = "Teal" });
        snapshot.Hubs.Add(new HubInfo { Id = "hub-nordics", Title = "Nordics", SiteUrl = root + "nordics", ParentHubId = "hub-emea", ThemeName = "Green" });
        snapshot.Sites.Add(Comm(root + "sales", "Sales Portal", "hub-sales", t.AddDays(-20), hub: true, owners: 2));
        snapshot.Sites.Add(Comm(root + "emea", "EMEA sales", "hub-emea", t.AddDays(-60), hub: true, owners: 1));
        snapshot.Sites.Add(Comm(root + "nordics", "Nordics", "hub-nordics", t.AddDays(-400), hub: true, owners: 0));

        snapshot.Sites.Add(new SiteInfo
        {
            Url = root + "allcompany",
            Title = "All Company",
            Template = "GROUP#0",
            Kind = SiteKind.TeamGroup,
            OwnerCount = 0,
            IsPublic = true,
            SubsiteCount = 0,
            LastActivity = t.AddDays(-12),
            Permissions = new PermissionStats { UniqueScopes = 85, EeeuGrants = 2, EveryoneGrants = 0, AnyoneLinks = 15 },
            Content = new ContentStats { Libraries = 3, Lists = 2, MaxFolderDepth = 4, LibrariesWithoutVersioning = 0 },
        });
        snapshot.Sites.Add(new SiteInfo
        {
            Url = root + "partners",
            Title = "Partner portal",
            Template = "GROUP#0",
            Kind = SiteKind.TeamGroup,
            HubId = "hub-intranet",
            IsExtranet = true,
            OwnerCount = 1,
            SharingLevel = SharingLevel.Anyone,
            LastActivity = t.AddDays(-30),
            Permissions = new PermissionStats { AnyoneLinks = 220, GuestCount = 140 },
        });

        for (var i = 1; i <= 24; i++)
        {
            snapshot.Sites.Add(new SiteInfo
            {
                Url = $"{root}project{i}",
                Title = $"Project {i}",
                Template = i % 3 == 0 ? "STS#0" : "GROUP#0",
                Kind = i % 3 == 0 ? SiteKind.Classic : SiteKind.TeamGroup,
                HubId = i % 4 == 0 ? "hub-intranet" : null,
                OwnerCount = i % 5 == 0 ? 0 : 1,
                SubsiteCount = i % 3 == 0 ? 2 : 0,
                LastActivity = t.AddDays(-(i * 30)),
                UsesClassicTheme = i % 3 == 0,
                Permissions = new PermissionStats { UniqueScopes = i * 3, EeeuGrants = i % 6 == 0 ? 1 : 0, EveryoneGrants = 0, AnyoneLinks = i % 4 },
                Content = new ContentStats { Libraries = 2, Lists = 3, MaxFolderDepth = 1 + (i % 5), LibrariesWithoutVersioning = i % 7 == 0 ? 1 : 0, ClassicPages = i % 3 == 0 ? 5 : 0 },
            });
        }

        return snapshot;
    }

    /// <summary>A well-kept intranet generated from the blueprint questionnaire, with a few realistic gaps.</summary>
    public static TenantSnapshot Northwind(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var blueprint = BlueprintGenerator.Generate(new IntranetAnswers
        {
            Organization = "Northwind",
            TenantPrefix = "northwind",
            Employees = 1200,
            Departments = ["HR", "IT", "Finance", "Communications", "Sales"],
            Regions = ["Stockholm", "Oslo", "Copenhagen"],
            Goals = ["news", "policies", "onboarding", "collaboration"],
            BrandColor = "#0f6cbd",
        });
        var snapshot = blueprint.ToSnapshot();
        snapshot.Source = SnapshotSource.Sample;
        snapshot.CapturedAt = t;
        snapshot.Settings.Microsoft365ArchiveEnabled = true;
        snapshot.Settings.BackupEnabled = true;
        snapshot.Settings.DaysSinceContentAssessment = 12;
        snapshot.Settings.SelfServiceSiteCreation = true;
        snapshot.Settings.LicensedUsers = 1200;
        snapshot.Settings.StorageQuotaMb = 13_312_000;
        snapshot.Settings.StorageUsedMb = 4_100_000;
        snapshot.Settings.SearchBookmarks = 42;
        snapshot.Settings.SearchAcronyms = 18;
        var random = new Random(7);
        foreach (var site in snapshot.Sites)
        {
            site.LastActivity = t.AddDays(-random.Next(0, 60));
            site.Permissions.EeeuGrants = site.IsPublic == true ? 0 : site.Permissions.EeeuGrants;
            site.Permissions.AnyoneLinks = 0;
            site.SensitivityLabel = site.IsPublic == true ? "General" : "Confidential";
            site.Content = new ContentStats { Pages = random.Next(5, 60), ClassicPages = 0, Lists = 3, Libraries = 2, MaxFolderDepth = 2, LibrariesWithoutVersioning = 0, HomePageWebParts = 12, LastNewsPublished = t.AddDays(-random.Next(1, 20)), HasDefaultNavigation = false };
        }

        // The gaps a real, well-run tenant still has.
        snapshot.Sites[^1].OwnerCount = 1;
        snapshot.Sites[^2].LastActivity = t.AddDays(-220);
        return snapshot;
    }

    private static SiteInfo Comm(string url, string title, string hubId, DateTimeOffset lastActivity, bool hub, int owners) => new()
    {
        Url = url,
        Title = title,
        Template = "SITEPAGEPUBLISHING#0",
        Kind = SiteKind.Communication,
        IsHub = hub,
        HubId = hubId,
        OwnerCount = owners,
        SubsiteCount = 0,
        IsPublic = true,
        LastActivity = lastActivity,
        MenuStyle = MenuStyle.Mega,
        Navigation = [new NavNode { Title = "Home", Url = url }, new NavNode { Title = "Documents", Url = url + "/Shared Documents" }, new NavNode { Title = "Pages", Url = url + "/SitePages" }],
        Content = new ContentStats { HasDefaultNavigation = true, Pages = 14, ClassicPages = 0, LastNewsPublished = lastActivity.AddDays(-90), HomePageWebParts = 9 },
        Permissions = new PermissionStats { UniqueScopes = 4, EeeuGrants = 1, EveryoneGrants = 0, AnyoneLinks = 0 },
    };

    private static ThemeInfo Theme(string name, string primary)
    {
        var theme = ThemeGenerator.Generate(new ThemeInput(name, Rgb.Parse(primary)));
        return new ThemeInfo { Name = name, Palette = new(theme.Palette) };
    }
}
