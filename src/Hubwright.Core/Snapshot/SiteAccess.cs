using System.Globalization;
using System.Text;

namespace Hubwright.Snapshot;

/// <summary>How much of a site the scan could read with the scanning person's access.</summary>
public enum SiteAccessLevel
{
    /// <summary>Access denied to the site itself: only the usage report's activity and storage are known.</summary>
    CannotOpen,

    /// <summary>The site was read, but its permissions answered access denied (a visitor or member, not an owner).</summary>
    CannotReadPermissions,
}

/// <summary>A site the person who scanned lacks permissions on, so part or all of it was not assessed.</summary>
public sealed record UnreadSite(string Url, string Title, SiteKind Kind, DateTimeOffset? LastActivity, long? StorageUsedMb, SiteAccessLevel Level, string Reason);

/// <summary>
/// The sites a scan could not fully read with the person's access (the scan reads as the person,
/// and Hubwright never gives itself access): sites it couldn't open at all, and sites whose
/// permissions it couldn't read. Shown as a warning wherever scores and findings are, so nobody reads
/// a score as covering them. Getting access is Microsoft's route through the admin center; the person
/// decides, Hubwright doesn't do it.
/// </summary>
public static class SiteAccess
{
    /// <summary>Microsoft's link to Active sites in the SharePoint admin center (Manage site admins, Learn).</summary>
    public const string ActiveSitesLink = "https://go.microsoft.com/fwlink/?linkid=2185220";

    public const string ManageSiteAdminsDoc = "https://learn.microsoft.com/en-us/sharepoint/manage-site-collection-administrators";

    public const string SitePermissionsDoc = "https://learn.microsoft.com/en-us/sharepoint/site-permissions";

    /// <summary>What a site admin can see (Admin center site permissions reference, reviewed 2026-10-31).</summary>
    public const string SiteAdminQuote = "They also have access to any items in the site, including in subsites, even if permissions inheritance has been broken.";

    /// <summary>The steps from Manage site admins, for every page and report that lists these sites.</summary>
    public static IReadOnlyList<string> HowToGetAccess { get; } =
    [
        "Open Active sites in the SharePoint admin center and select the site.",
        "Membership (on the command bar) shows its owners and site admins without joining it: often that is all you need, or ask an owner to check it.",
        "To read it in a scan, add yourself or a scanning account as a site admin there, then scan again. About site admins, Microsoft says: \"" + SiteAdminQuote + "\" So agree it with the owner, and remove it afterwards.",
    ];

    public const string Explanation = "The scan reads as you. Where you are not a member, or are a member but not an owner, SharePoint refused part of the site, so those parts were not read and their rules are not assessed: left out of the score, never counted as passed. Private sites are often the ones that need a look most.";

    /// <summary>Sites you can't open first, then those whose permissions you can't read; the most recently active first within each.</summary>
    public static IReadOnlyList<UnreadSite> Unread(TenantSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var sites = new List<UnreadSite>();
        foreach (var site in snapshot.Sites)
        {
            if (site.Readable == false)
            {
                sites.Add(new UnreadSite(site.Url, site.Title, site.Kind, site.LastActivity, site.StorageUsedMb, SiteAccessLevel.CannotOpen,
                    Denied(snapshot, ScanAreas.SiteDetail, site.Url)?.Reason ?? "Not readable with your access."));
            }
            else if (Denied(snapshot, ScanAreas.Permissions, site.Url) is { } issue)
            {
                sites.Add(new UnreadSite(site.Url, site.Title, site.Kind, site.LastActivity, site.StorageUsedMb, SiteAccessLevel.CannotReadPermissions, issue.Reason));
            }
        }

        return [.. sites.OrderBy(s => s.Level).ThenByDescending(s => s.LastActivity ?? DateTimeOffset.MinValue).ThenBy(s => s.Url, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The heading: "12 sites you lack permissions on (3 you can't open, 9 whose permissions you can't read)".</summary>
    public static string Summary(IReadOnlyCollection<UnreadSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        var open = sites.Count(s => s.Level == SiteAccessLevel.CannotOpen);
        var permissions = sites.Count - open;
        var parts = new List<string>();
        if (open > 0)
        {
            parts.Add($"{open.ToString(CultureInfo.InvariantCulture)} you can't open");
        }

        if (permissions > 0)
        {
            parts.Add($"{permissions.ToString(CultureInfo.InvariantCulture)} whose permissions you can't read");
        }

        return $"{sites.Count.ToString(CultureInfo.InvariantCulture)} site{(sites.Count == 1 ? "" : "s")} you lack permissions on, not fully assessed ({string.Join(", ", parts)})";
    }

    /// <summary>What was not read at this level, for a list row.</summary>
    public static string Missing(SiteAccessLevel level) => level == SiteAccessLevel.CannotOpen
        ? "can't open: owners, permissions, sharing and content not read"
        : "can't read its permissions: who has access and sharing not read";

    public static string Kind(SiteKind kind) => kind switch
    {
        SiteKind.Communication => "communication",
        SiteKind.TeamGroup => "team (group)",
        SiteKind.TeamNoGroup => "team",
        SiteKind.Classic => "classic",
        _ => kind.ToString().ToLowerInvariant(),
    };

    public static string Csv(IEnumerable<UnreadSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        static string F(string? v) => v is null ? "" : v.Contains(',', StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal) ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;
        var sb = new StringBuilder("Title,Url,Kind,Access,LastActivity,StorageMB,Reason\n");
        foreach (var s in sites)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{F(s.Title)},{F(s.Url)},{F(Kind(s.Kind))},{F(Missing(s.Level))},{s.LastActivity?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},{s.StorageUsedMb?.ToString(CultureInfo.InvariantCulture)},{F(s.Reason)}\n");
        }

        return sb.ToString();
    }

    private static ScanIssue? Denied(TenantSnapshot snapshot, string area, string url)
        => snapshot.ScanIssues.FirstOrDefault(i => i.Area == area && i.Target is { } t && string.Equals(t.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && (i.Reason.StartsWith("Access denied", StringComparison.OrdinalIgnoreCase) || i.Reason.Contains("403", StringComparison.Ordinal)));
}
