using System.Text.Json;
using Hubwright.Snapshot;
using Hubwright.Theming;

namespace Hubwright.Blueprints;

/// <summary>The answers the from-scratch questionnaire collects.</summary>
public sealed class IntranetAnswers
{
    public string Organization { get; set; } = "Contoso";

    /// <summary>Tenant host prefix: <c>contoso</c> for contoso.sharepoint.com.</summary>
    public string TenantPrefix { get; set; } = "contoso";

    public int Employees { get; set; } = 500;

    public List<string> Departments { get; set; } = ["HR", "IT", "Finance", "Communications", "Sales"];

    public List<string> Regions { get; set; } = [];

    public List<string> Languages { get; set; } = ["en-US"];

    /// <summary>news, policies, onboarding, collaboration, copilot, communities.</summary>
    public List<string> Goals { get; set; } = ["news", "policies", "onboarding"];

    public bool ExternalCollaboration { get; set; }

    public bool AllowAnyoneLinks { get; set; }

    public string BrandColor { get; set; } = "#0f6cbd";

    /// <summary>Page background for the theme; white unless the theme editor chose another.</summary>
    public string BackgroundColor { get; set; } = "#ffffff";

    /// <summary>The theme's name in the tenant; "{Organization} brand" when empty.</summary>
    public string? ThemeName { get; set; }

    /// <summary>A palette edited slot by slot in the theme editor; replaces the generated one when set.</summary>
    public Dictionary<string, string>? Palette { get; set; }

    /// <summary>The site logo, relative to the proposal folder (assets/logo.png).</summary>
    public string? Logo { get; set; }

    /// <summary>Whether the logo is square, so it can be the site logo thumbnail too.</summary>
    public bool LogoIsSquare { get; set; }

    /// <summary>minimal, compact, standard, extended.</summary>
    public string HeaderLayout { get; set; } = "standard";

    public bool HasGoal(string goal) => Goals.Exists(g => string.Equals(g, goal, StringComparison.OrdinalIgnoreCase));
}

public sealed class BlueprintSite
{
    public string Title { get; set; } = "";

    public string Slug { get; set; } = "";

    public SiteKind Kind { get; set; } = SiteKind.Communication;

    public string Purpose { get; set; } = "";

    /// <summary>Private team space: kept out of hub navigation or shown only to its members.</summary>
    public bool Private { get; set; }

    public bool Extranet { get; set; }

    /// <summary>The site this already is in the tenant, when a proposal keeps it instead of creating it.</summary>
    public string? ExistingUrl { get; set; }

    /// <summary>The ready-made template suggested for it (a <c>templates.yaml</c> id).</summary>
    public string? Template { get; set; }
}

public sealed class BlueprintHub
{
    public string Title { get; set; } = "";

    public string Slug { get; set; } = "";

    public string Purpose { get; set; } = "";

    /// <summary>The hub this hub rolls up to (hub-to-hub association), by slug.</summary>
    public string? Parent { get; set; }

    public List<BlueprintSite> Sites { get; set; } = [];

    public List<NavNode> Navigation { get; set; } = [];

    /// <summary>The hub site that already exists, when a proposal reuses it.</summary>
    public string? ExistingUrl { get; set; }
}

/// <summary>Sharing and lifecycle choices the blueprint sets tenant-wide.</summary>
public sealed class BlueprintPolicy
{
    public SharingLevel Sharing { get; set; } = SharingLevel.ExistingGuests;

    public DefaultLinkType DefaultLink { get; set; } = DefaultLinkType.Organization;

    public int AnyoneLinkExpirationDays { get; set; } = 30;

    public int GuestAccessExpirationDays { get; set; } = 90;

    public int MinimumOwners { get; set; } = 2;

    public int InactiveMonths { get; set; } = 6;

    public string NamingConvention { get; set; } = "{Function} Hub for hubs; {Function} – {Topic} for sites";
}

/// <summary>
/// A target intranet: the home site, hubs and their sites, navigation, policies and theme. It can
/// become a <see cref="TenantSnapshot"/> (<see cref="ToSnapshot"/>), so the same rules that assess a
/// tenant also check the design before anything is built.
/// </summary>
public sealed class Blueprint
{
    public string Name { get; set; } = "";

    public string Organization { get; set; } = "";

    public string TenantPrefix { get; set; } = "contoso";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public BlueprintSite Home { get; set; } = new();

    public bool HomeIsHub { get; set; }

    public List<NavNode> GlobalNavigation { get; set; } = [];

    public List<BlueprintHub> Hubs { get; set; } = [];

    /// <summary>Sites kept out of every hub on purpose (extranets, one-off projects).</summary>
    public List<BlueprintSite> Standalone { get; set; } = [];

    public BlueprintPolicy Policy { get; set; } = new();

    public string BrandColor { get; set; } = "#0f6cbd";

    /// <summary>Page background for the theme; white unless the theme editor chose another.</summary>
    public string BackgroundColor { get; set; } = "#ffffff";

    /// <summary>The theme's name in the tenant; "{Organization} brand" when empty.</summary>
    public string? ThemeName { get; set; }

    /// <summary>A palette edited slot by slot in the theme editor; replaces the generated one when set.</summary>
    public Dictionary<string, string>? Palette { get; set; }

    /// <summary>The site logo, relative to the proposal folder (assets/logo.png).</summary>
    public string? Logo { get; set; }

    /// <summary>Whether the logo is square, so it can be the site logo thumbnail too.</summary>
    public bool LogoIsSquare { get; set; }

    public string HeaderLayout { get; set; } = "standard";

    public List<string> Languages { get; set; } = ["en-US"];

    /// <summary>Why the generator chose this shape, one line per decision, for the report and the AI.</summary>
    public List<string> Rationale { get; set; } = [];

    public string Url(string slug) => $"https://{TenantPrefix}.sharepoint.com/sites/{slug}";

    public string ToJson() => JsonSerializer.Serialize(this, TenantSnapshot.Json);

    public static Blueprint FromJson(string json)
        => JsonSerializer.Deserialize<Blueprint>(json, TenantSnapshot.Json) ?? throw new InvalidDataException("The blueprint file is empty.");

    public SharePointTheme Theme()
    {
        var name = string.IsNullOrWhiteSpace(ThemeName) ? $"{Organization} brand" : ThemeName;
        if (Palette is { Count: > 0 })
        {
            return ThemeGenerator.FromJson(JsonSerializer.Serialize(new { name, palette = Palette }), name);
        }

        var background = Rgb.TryParse(BackgroundColor, out var bg) ? bg : Rgb.White;
        return ThemeGenerator.Generate(new ThemeInput(name, Rgb.Parse(BrandColor))
        {
            Background = background,
            Text = background.IsDark ? new Rgb(0xf3, 0xf2, 0xf1) : new Rgb(0x32, 0x31, 0x30),
        });
    }

    /// <summary>The blueprint as if it were built, for rule checks and previews. Content facts stay unknown.</summary>
    public TenantSnapshot ToSnapshot()
    {
        var theme = Theme();
        var snapshot = new TenantSnapshot
        {
            Tenant = Organization,
            Source = SnapshotSource.Blueprint,
            GlobalNavigation = GlobalNavigation,
            Settings = new TenantSettings
            {
                SharingLevel = Policy.Sharing,
                DefaultLinkType = Policy.DefaultLink,
                DefaultLinkPermission = LinkAccess.View,
                AnyoneLinkExpirationDays = Policy.Sharing == SharingLevel.Anyone ? Policy.AnyoneLinkExpirationDays : null,
                AnyoneFileLinkPermission = Policy.Sharing == SharingLevel.Anyone ? LinkAccess.View : null,
                AnyoneFolderLinkPermission = Policy.Sharing == SharingLevel.Anyone ? LinkAccess.View : null,
                GuestAccessExpirationDays = Policy.Sharing == SharingLevel.OnlyOrganization ? null : Policy.GuestAccessExpirationDays,
                CustomScriptAllowed = false,
                HideDefaultThemes = true,
                AppBarEnabled = GlobalNavigation.Count > 0,
                InactiveSitePolicies = 1,
                SiteOwnershipPolicies = 1,
                SiteAttestationPolicies = 1,
            },
            Themes = [new ThemeInfo { Name = theme.Name, IsInverted = theme.IsInverted, Palette = new(theme.Palette) }],
        };

        snapshot.Sites.Add(Site(Home, isHome: true, hubId: null, isHub: HomeIsHub, theme.Name));
        foreach (var hub in Hubs)
        {
            snapshot.Hubs.Add(new HubInfo
            {
                Id = hub.Slug,
                Title = hub.Title,
                SiteUrl = hub.ExistingUrl ?? Url(hub.Slug),
                ParentHubId = hub.Parent,
                ThemeName = theme.Name,
                Navigation = hub.Navigation,
            });
            snapshot.Sites.Add(Site(new BlueprintSite { Title = hub.Title, Slug = hub.Slug, Purpose = hub.Purpose, ExistingUrl = hub.ExistingUrl }, isHome: false, hubId: hub.Slug, isHub: true, theme.Name));
            snapshot.Sites.AddRange(hub.Sites.Select(s => Site(s, isHome: false, hubId: hub.Slug, isHub: false, theme.Name)));
        }

        snapshot.Sites.AddRange(Standalone.Select(s => Site(s, isHome: false, hubId: null, isHub: false, theme.Name)));
        return snapshot;
    }

    private SiteInfo Site(BlueprintSite site, bool isHome, string? hubId, bool isHub, string themeName) => new()
    {
        Url = site.ExistingUrl ?? Url(site.Slug),
        Title = site.Title,
        Description = site.Purpose,
        Template = site.Kind == SiteKind.Communication ? "SITEPAGEPUBLISHING#0" : "GROUP#0",
        Kind = site.Kind,
        IsHomeSite = isHome,
        IsHub = isHub,
        HubId = hubId,
        IsExtranet = site.Extranet,
        IsPublic = !site.Private,
        SensitivityLabel = site.Extranet ? "Partner" : site.Private ? "Confidential" : "General",
        OwnerCount = Policy.MinimumOwners,
        SubsiteCount = 0,
        SharingLevel = site.Extranet ? SharingLevel.NewAndExistingGuests : null,
        UsesClassicTheme = false,
        ThemeName = themeName,
        MenuStyle = MenuStyle.Mega,
        Permissions = new PermissionStats { EveryoneGrants = 0, EeeuGrants = site.Private ? 0 : null, AnyoneLinks = 0, UniqueScopes = 0, MaxUniqueScopesInList = 0 },
    };
}
