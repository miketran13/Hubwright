using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hubwright.Snapshot;

/// <summary>Where a snapshot came from: a live scan, an offline export, or a blueprint turned into a tenant.</summary>
public enum SnapshotSource { Scan, Import, Blueprint, Sample }

/// <summary>
/// Everything Hubwright knows about a tenant at one moment. Collected once, then every rule, report,
/// diff and chat answer reads this, never the tenant. Values that could not be read stay null and
/// are listed in <see cref="ScanIssues"/>, so a rule over them is "not assessed" rather than "pass".
/// </summary>
public sealed class TenantSnapshot
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    public string Tenant { get; set; } = "";

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    public SnapshotSource Source { get; set; } = SnapshotSource.Scan;

    /// <summary>The scope when only part of the tenant was scanned (a hub or site URL); null for the whole tenant.</summary>
    public string? Scope { get; set; }

    public TenantSettings Settings { get; set; } = new();

    public List<SiteInfo> Sites { get; set; } = [];

    public List<HubInfo> Hubs { get; set; } = [];

    /// <summary>Global navigation (the SharePoint app bar), from the home site.</summary>
    public List<NavNode> GlobalNavigation { get; set; } = [];

    public List<ThemeInfo> Themes { get; set; } = [];

    public List<ScanIssue> ScanIssues { get; set; } = [];

    [JsonIgnore]
    public SiteInfo? HomeSite => Sites.FirstOrDefault(s => s.IsHomeSite);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Whether a scan issue says this area was not read for this one site or hub (not a tenant-wide issue).</summary>
    public bool Unread(string area, string target)
        => ScanIssues.Exists(i => i.Area == area && i.Target is { } t && string.Equals(t.TrimEnd('/'), target.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a scan issue says the site list itself is incomplete, so tenant-wide site counts are unknown.</summary>
    [JsonIgnore]
    public bool SiteListIncomplete => ScanIssues.Exists(i => i.Area == ScanAreas.Sites && i.Target is null);

    public static TenantSnapshot FromJson(string json)
    {
        var snapshot = JsonSerializer.Deserialize<TenantSnapshot>(json, Json)
            ?? throw new InvalidDataException("The snapshot file is empty.");
        if (snapshot.Schema > CurrentSchema)
        {
            throw new InvalidDataException($"Snapshot schema {snapshot.Schema} is newer than this Hubwright ({CurrentSchema}). Update Hubwright.");
        }

        return snapshot;
    }
}

/// <summary>The areas a scan issue can name; rules over an area with an issue are not assessed.</summary>
public static class ScanAreas
{
    public const string Settings = "settings";
    public const string Sites = "sites";
    public const string Hubs = "hubs";
    public const string HomeSite = "home-site";
    public const string GlobalNavigation = "global-navigation";
    public const string Activity = "activity";
    public const string Labels = "labels";
    public const string Themes = "themes";
    public const string SiteDetail = "site-detail";
    public const string Navigation = "navigation";
    public const string Permissions = "permissions";
    public const string Content = "content";
}

/// <summary>Something a collector could not read, and why: a missing scope, a role, throttling.</summary>
public sealed record ScanIssue(string Area, string Reason)
{
    public string? Target { get; init; }
}

/// <summary>SharePoint sharing levels, most to least permissive (SPO <c>SharingCapability</c>).</summary>
public enum SharingLevel { Anyone, NewAndExistingGuests, ExistingGuests, OnlyOrganization }

/// <summary>The link type preselected in the share dialog (SPO <c>DefaultSharingLinkType</c>).</summary>
public enum DefaultLinkType { Anyone, Organization, SpecificPeople }

public enum LinkAccess { View, Edit, ViewAndUpload }

/// <summary>Tenant-wide settings from the SharePoint admin API. Null means not read.</summary>
public sealed class TenantSettings
{
    public SharingLevel? SharingLevel { get; set; }

    public DefaultLinkType? DefaultLinkType { get; set; }

    public LinkAccess? DefaultLinkPermission { get; set; }

    /// <summary>Days before Anyone links expire; 0 means they never expire.</summary>
    public int? AnyoneLinkExpirationDays { get; set; }

    public LinkAccess? AnyoneFileLinkPermission { get; set; }

    public LinkAccess? AnyoneFolderLinkPermission { get; set; }

    /// <summary>Days before guest access expires; 0 means never.</summary>
    public int? GuestAccessExpirationDays { get; set; }

    public bool? CustomScriptAllowed { get; set; }

    public bool? HideDefaultThemes { get; set; }

    public bool? SelfServiceSiteCreation { get; set; }

    public bool? RestrictedAccessControlEnabled { get; set; }

    public bool? Microsoft365ArchiveEnabled { get; set; }

    public bool? SharePointAdvancedManagement { get; set; }

    public int? InactiveSitePolicies { get; set; }

    public int? SiteOwnershipPolicies { get; set; }

    public int? SiteAttestationPolicies { get; set; }

    public bool? SensitivityLabelsEnabled { get; set; }

    public bool? DlpBlocksAnyoneLinks { get; set; }

    public bool? BackupEnabled { get; set; }

    /// <summary>Days since the Content Management Assessment last ran; null when never run or unknown.</summary>
    public int? DaysSinceContentAssessment { get; set; }

    public int? LicensedUsers { get; set; }

    public long? StorageQuotaMb { get; set; }

    public long? StorageUsedMb { get; set; }

    /// <summary>Microsoft Search answers: bookmarks and acronyms.</summary>
    public int? SearchBookmarks { get; set; }

    public int? SearchAcronyms { get; set; }

    public bool? AppBarEnabled { get; set; }
}

/// <summary>What kind of site this is, from its web template.</summary>
public enum SiteKind { Communication, TeamGroup, TeamNoGroup, Channel, Classic, Other }

public enum MenuStyle { Mega, Cascading }

public enum LockState { Unlocked, ReadOnly, NoAccess }

public sealed class SiteInfo
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Web template, for example SITEPAGEPUBLISHING#0, GROUP#0, STS#3, STS#0.</summary>
    public string Template { get; set; } = "";

    public SiteKind Kind { get; set; } = SiteKind.Other;

    public bool IsHub { get; set; }

    public bool IsHomeSite { get; set; }

    /// <summary>The hub this site is associated with; null when it is in no hub.</summary>
    public string? HubId { get; set; }

    public bool IsExtranet { get; set; }

    /// <summary>
    /// Whether the person who scanned could read the site itself. False when they are not a member
    /// (it came back access denied), so only what the tenant-wide usage report says about it is known:
    /// its owners, navigation, content and permissions stay null. Null when it was not tried.
    /// </summary>
    public bool? Readable { get; set; }

    public bool? IsPublic { get; set; }

    public int? OwnerCount { get; set; }

    public int? MemberCount { get; set; }

    public string? SensitivityLabel { get; set; }

    public long? StorageUsedMb { get; set; }

    public DateTimeOffset? Created { get; set; }

    public DateTimeOffset? LastActivity { get; set; }

    public int? SubsiteCount { get; set; }

    /// <summary>The site's own sharing level; more permissive than the tenant's is a finding.</summary>
    public SharingLevel? SharingLevel { get; set; }

    public LockState LockState { get; set; } = LockState.Unlocked;

    public bool? RestrictedContentDiscovery { get; set; }

    public bool? RestrictedAccessControl { get; set; }

    public bool? UsesClassicTheme { get; set; }

    public string? ThemeName { get; set; }

    public MenuStyle? MenuStyle { get; set; }

    public List<NavNode> Navigation { get; set; } = [];

    public PermissionStats Permissions { get; set; } = new();

    public ContentStats Content { get; set; } = new();
}

/// <summary>Permission facts per site, sampled to the configured depth.</summary>
public sealed class PermissionStats
{
    /// <summary>Unique permission scopes (broken inheritance) across lists, folders and items.</summary>
    public int? UniqueScopes { get; set; }

    /// <summary>The most unique scopes in any single list or library.</summary>
    public int? MaxUniqueScopesInList { get; set; }

    public int? EveryoneGrants { get; set; }

    /// <summary>Grants to "Everyone except external users".</summary>
    public int? EeeuGrants { get; set; }

    public int? AnyoneLinks { get; set; }

    public int? OrganizationLinks { get; set; }

    public int? SpecificPeopleLinks { get; set; }

    public int? GuestCount { get; set; }
}

public sealed class ContentStats
{
    public int? Pages { get; set; }

    public int? ClassicPages { get; set; }

    public DateTimeOffset? LastNewsPublished { get; set; }

    public int? Lists { get; set; }

    public int? Libraries { get; set; }

    public int? MaxFolderDepth { get; set; }

    public long? LargestListItems { get; set; }

    public int? LibrariesWithoutVersioning { get; set; }

    public int? HomePageWebParts { get; set; }

    public int? HomePageImagesOver300Kb { get; set; }

    public bool? HomePageHasEmbeds { get; set; }

    public long? Files { get; set; }

    public int? MaxPathLength { get; set; }

    /// <summary>True while a communication site still shows the out-of-box Documents / Pages / Site contents links.</summary>
    public bool? HasDefaultNavigation { get; set; }

    public int? NavLinksToDocuments { get; set; }

    public bool? UsesClassicPublishing { get; set; }

    public bool? UsesInfoPath { get; set; }

    public bool? UsesClassicWorkflows { get; set; }
}

public sealed class HubInfo
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string SiteUrl { get; set; } = "";

    /// <summary>The hub this hub is associated with (hub-to-hub), if any.</summary>
    public string? ParentHubId { get; set; }

    public string? ThemeName { get; set; }

    public List<NavNode> Navigation { get; set; } = [];
}

public sealed class NavNode
{
    public string Title { get; set; } = "";

    /// <summary>Null for a label: a heading that groups links but is not a link.</summary>
    public string? Url { get; set; }

    public bool AudienceTargeted { get; set; }

    /// <summary>Points at a site the reader may not have access to (private, extranet).</summary>
    public bool Restricted { get; set; }

    public List<NavNode> Children { get; set; } = [];

    [JsonIgnore]
    public bool IsLabel => string.IsNullOrWhiteSpace(Url);
}

/// <summary>A theme the tenant offers (Add-SPOTheme), by name.</summary>
public sealed class ThemeInfo
{
    public string Name { get; set; } = "";

    public bool IsInverted { get; set; }

    public Dictionary<string, string> Palette { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
