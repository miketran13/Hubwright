using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Hubwright.Http;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Collectors;

/// <summary>Progress while a scan runs: what is being read now.</summary>
public sealed record ScanProgress(string Step, int Done = 0, int Total = 0);

/// <summary>
/// Reads a tenant into a <see cref="TenantSnapshot"/>, read-only, as the signed-in admin. Each area
/// is read on its own. When one fails (missing role, scope, throttling, an endpoint the tenant does
/// not have), the collector records a <see cref="ScanIssue"/> and carries on, so the report says
/// "not assessed" instead of guessing.
/// <para>
/// It reads as the person, so it sees what they see. Search lists only the sites they can open; the
/// usage report adds the rest, and a site that answers access denied is marked
/// <see cref="SiteInfo.Readable"/> false with its details left null. It never gives itself access.
/// </para>
/// <para>
/// Endpoints: SharePoint admin REST (<c>/_api/SPO.Tenant</c>, the theme manager), SharePoint REST on
/// each site (web, navigation, lists, role assignments, Site Pages), search (<c>contentclass:STS_Site</c>),
/// <c>/_api/SP.HubSites</c>, the home site, and the Graph SharePoint site usage report for activity.
/// They have not yet been checked against a live tenant; the offline PnP collector
/// (tools/collect-snapshot.ps1) is the dependable route until they are.
/// </para>
/// </summary>
public sealed class TenantCollector(TenantHttp http, TenantConnection tenant, ScanOptions options)
{
    public async Task<TenantSnapshot> CollectAsync(string? scope, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var snapshot = new TenantSnapshot { Tenant = tenant.Name ?? "", Source = SnapshotSource.Scan, Scope = scope };

        progress?.Report(new("Tenant settings"));
        await TryAsync(snapshot, ScanAreas.Settings, null, () => ReadSettingsAsync(snapshot, cancellationToken)).ConfigureAwait(false);

        progress?.Report(new("Themes"));
        await TryAsync(snapshot, ScanAreas.Themes, null, () => ReadThemesAsync(snapshot, cancellationToken)).ConfigureAwait(false);

        progress?.Report(new("Hubs"));
        await TryAsync(snapshot, ScanAreas.Hubs, null, () => ReadHubsAsync(snapshot, cancellationToken)).ConfigureAwait(false);

        progress?.Report(new("Sites"));
        var searched = await TryAsync(snapshot, ScanAreas.Sites, null, () => ReadSitesAsync(snapshot, scope, cancellationToken)).ConfigureAwait(false);

        // Search only finds sites the person can open; the usage report lists every site, so it fills the gaps.
        progress?.Report(new("Site inventory and activity (usage report)"));
        var reportOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = await TryAsync(snapshot, ScanAreas.Activity, null, () => ReadUsageReportAsync(snapshot, scope, reportOnly, cancellationToken)).ConfigureAwait(false);
        if (!listed && searched)
        {
            snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Sites, IncompleteSiteList("the usage report could not be read (it needs Reports.Read.All and a role such as SharePoint Administrator or Reports Reader)")));
        }

        progress?.Report(new("Home site"));
        await TryAsync(snapshot, ScanAreas.HomeSite, null, () => ReadHomeSiteAsync(snapshot, cancellationToken)).ConfigureAwait(false);

        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Labels, "Sensitivity labels are not read by the live collector yet; use the PnP collector or set them in the snapshot."));
        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.GlobalNavigation, "Global navigation (app bar) is not read by the live collector yet."));

        var detailed = snapshot.Sites.OrderByDescending(s => s.IsHub || s.IsHomeSite).ThenByDescending(s => s.LastActivity).Take(options.MaxDetailedSites).ToList();
        foreach (var skipped in snapshot.Sites.Except(detailed))
        {
            snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.SiteDetail, $"Not read in detail (scan.maxDetailedSites = {options.MaxDetailedSites}).") { Target = skipped.Url });
            if (reportOnly.Contains(skipped.Url) && !skipped.IsHub)
            {
                snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Hubs, HubNotRead) { Target = skipped.Url });
            }
        }

        var done = 0;
        var issues = new ConcurrentBag<ScanIssue>();
        await Parallel.ForEachAsync(detailed, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = cancellationToken }, async (site, token) =>
        {
            await ReadSiteDetailAsync(site, reportOnly.Contains(site.Url), issues, token).ConfigureAwait(false);
            progress?.Report(new("Site details", Interlocked.Increment(ref done), detailed.Count));
        }).ConfigureAwait(false);
        snapshot.ScanIssues.AddRange(issues);

        foreach (var hub in snapshot.Hubs)
        {
            if (string.IsNullOrWhiteSpace(hub.SiteUrl))
            {
                snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Navigation, $"The hub \"{hub.Title}\" came back without a site URL (its site may be one you can't open, or deleted), so its navigation was not read.") { Target = "" });
                continue;
            }

            await TryAsync(snapshot, ScanAreas.Navigation, hub.SiteUrl, () => ReadHubNavigationAsync(hub, cancellationToken)).ConfigureAwait(false);
        }

        snapshot.CapturedAt = DateTimeOffset.UtcNow;
        return snapshot;
    }

    private const string HubNotRead = "Hub association not read: the site is not in your search results and its details were not read.";

    private static string IncompleteSiteList(string why)
        => $"Only sites you can access are listed: search hides the others, and {why}. Site counts are not assessed.";

    /// <summary>Runs one read; on failure records a scan issue and returns false.</summary>
    private static async Task<bool> TryAsync(TenantSnapshot snapshot, string area, string? target, Func<Task> read)
    {
        try
        {
            await read().ConfigureAwait(false);
            return true;
        }
        catch (ConnectorException ex)
        {
            snapshot.ScanIssues.Add(new ScanIssue(area, ex.IsAccessDenied ? $"Access denied: {ex.Message}" : ex.Message) { Target = target });
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            snapshot.ScanIssues.Add(new ScanIssue(area, ex.Message) { Target = target });
        }

        return false;
    }

    private async Task ReadSettingsAsync(TenantSnapshot snapshot, CancellationToken cancellationToken)
    {
        var t = await http.GetAsync($"{tenant.AdminUrl}/_api/SPO.Tenant", cancellationToken, sharePointVerbose: true).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Empty tenant settings.");
        var s = snapshot.Settings;
        s.SharingLevel = Int(t, "SharingCapability") switch
        {
            0 => SharingLevel.OnlyOrganization,
            1 => SharingLevel.NewAndExistingGuests,
            2 => SharingLevel.Anyone,
            3 => SharingLevel.ExistingGuests,
            _ => null,
        };
        s.DefaultLinkType = Int(t, "DefaultSharingLinkType") switch
        {
            1 => DefaultLinkType.SpecificPeople,
            2 => DefaultLinkType.Organization,
            3 => DefaultLinkType.Anyone,
            _ => null,
        };
        s.DefaultLinkPermission = Int(t, "DefaultLinkPermission") switch { 1 => LinkAccess.View, 2 => LinkAccess.Edit, _ => null };
        s.AnyoneLinkExpirationDays = Int(t, "RequireAnonymousLinksExpireInDays") is { } days ? Math.Max(0, days) : null;
        s.AnyoneFileLinkPermission = Int(t, "FileAnonymousLinkType") switch { 1 => LinkAccess.View, 2 => LinkAccess.Edit, _ => null };
        s.AnyoneFolderLinkPermission = Int(t, "FolderAnonymousLinkType") switch { 1 => LinkAccess.View, 2 => LinkAccess.Edit, _ => null };
        s.GuestAccessExpirationDays = Bool(t, "ExternalUserExpirationRequired") switch
        {
            true => Int(t, "ExternalUserExpireInDays"),
            false => 0,
            _ => null,
        };
        s.SelfServiceSiteCreation = Bool(t, "SelfServiceSiteCreationDisabled") is { } disabled ? !disabled : null;
        s.StorageQuotaMb = Long(t, "StorageQuota");
    }

    private async Task ReadThemesAsync(TenantSnapshot snapshot, CancellationToken cancellationToken)
    {
        var options = await http.PostReadOnlyAsync($"{tenant.AdminUrl}/_api/thememanager/GetTenantThemingOptions", cancellationToken).ConfigureAwait(false);
        snapshot.Settings.HideDefaultThemes = Bool(options, "hideDefaultThemes");
        foreach (var preview in options?["themePreviews"]?.AsArray() ?? [])
        {
            if (preview?["themeJson"]?.GetValue<string>() is not { } json)
            {
                continue;
            }

            var theme = Theming.ThemeGenerator.FromJson(json, preview["name"]?.GetValue<string>() ?? "Theme");
            snapshot.Themes.Add(new ThemeInfo { Name = preview["name"]?.GetValue<string>() ?? theme.Name, IsInverted = theme.IsInverted, Palette = new(theme.Palette) });
        }
    }

    private async Task ReadHubsAsync(TenantSnapshot snapshot, CancellationToken cancellationToken)
    {
        var hubs = await http.GetAsync($"{tenant.RootUrl}/_api/SP.HubSites", cancellationToken, sharePointVerbose: true).ConfigureAwait(false);
        foreach (var hub in hubs?["value"]?.AsArray() ?? [])
        {
            if (hub is null)
            {
                continue;
            }

            var parent = Text(hub, "ParentHubSiteId");
            snapshot.Hubs.Add(new HubInfo
            {
                Id = Text(hub, "ID") ?? "",
                Title = Text(hub, "Title") ?? "",
                SiteUrl = Text(hub, "SiteUrl") ?? "",
                ParentHubId = parent is null || parent == Guid.Empty.ToString() ? null : parent,
            });
        }
    }

    private async Task ReadSitesAsync(TenantSnapshot snapshot, string? scope, CancellationToken cancellationToken)
    {
        const int pageSize = 500;
        var query = "contentclass:STS_Site" + (scope is null ? "" : $" Path:\"{scope}*\"");
        for (var start = 0; start < 50_000; start += pageSize)
        {
            var url = $"{tenant.RootUrl}/_api/search/query?querytext='{Uri.EscapeDataString(query)}'&selectproperties='Title,Path,WebTemplate,DepartmentId,Created,LastModifiedTime,Description'&rowlimit={pageSize}&startrow={start}&trimduplicates=false";
            var result = await http.GetAsync(url, cancellationToken, sharePointVerbose: true).ConfigureAwait(false);
            var rows = result?["PrimaryQueryResult"]?["RelevantResults"]?["Table"]?["Rows"]?.AsArray() ?? [];
            foreach (var row in rows)
            {
                var cells = (row?["Cells"]?.AsArray() ?? []).Where(c => c is not null).ToDictionary(c => c!["Key"]!.GetValue<string>(), c => c!["Value"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase);
                var path = cells.GetValueOrDefault("Path");
                if (string.IsNullOrWhiteSpace(path) || path.Contains("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var template = cells.GetValueOrDefault("WebTemplate") ?? "";
                var hubId = cells.GetValueOrDefault("DepartmentId")?.Trim('{', '}');
                snapshot.Sites.Add(new SiteInfo
                {
                    Url = path.TrimEnd('/'),
                    Title = cells.GetValueOrDefault("Title") ?? path,
                    Description = cells.GetValueOrDefault("Description"),
                    Template = template,
                    Kind = KindOf(template, null),
                    HubId = string.IsNullOrWhiteSpace(hubId) ? null : hubId,
                    Created = Date(cells.GetValueOrDefault("Created")),
                });
            }

            if (rows.Count < pageSize)
            {
                break;
            }
        }

        MarkHubs(snapshot);
    }

    private static void MarkHubs(TenantSnapshot snapshot)
    {
        foreach (var hub in snapshot.Hubs.Where(h => !string.IsNullOrWhiteSpace(h.SiteUrl)))
        {
            if (snapshot.Sites.FirstOrDefault(s => Same(s.Url, hub.SiteUrl)) is { } site)
            {
                site.IsHub = true;
                site.HubId = hub.Id;
            }
        }
    }

    private async Task ReadHomeSiteAsync(TenantSnapshot snapshot, CancellationToken cancellationToken)
    {
        var details = await http.GetAsync($"{tenant.RootUrl}/_api/SP.SPHSite/Details", cancellationToken, sharePointVerbose: true).ConfigureAwait(false);
        if (Text(details, "Url") is { } url && snapshot.Sites.FirstOrDefault(s => Same(s.Url, url)) is { } home)
        {
            home.IsHomeSite = true;
        }
    }

    /// <summary>
    /// The SharePoint site usage report: activity, storage and files for the sites already found, and
    /// the tenant-wide site list. Unlike search it is not trimmed to the sites the person can open
    /// ("read all service usage reports on behalf of the signed-in user", for a limited administrator
    /// role, https://learn.microsoft.com/en-us/graph/reportroot-authorization), so a site missing from
    /// search is added here (<paramref name="reportOnly"/>) and its details are tried like any other.
    /// </summary>
    private async Task ReadUsageReportAsync(TenantSnapshot snapshot, string? scope, HashSet<string> reportOnly, CancellationToken cancellationToken)
    {
        var csv = await http.GetTextAsync("https://graph.microsoft.com/v1.0/reports/getSharePointSiteUsageDetail(period='D180')", cancellationToken).ConfigureAwait(false);
        var lines = csv.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return;
        }

        var header = Csv(lines[0]);
        int Col(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        int url = Col("Site URL"), last = Col("Last Activity Date"), storage = Col("Storage Used (Byte)"), files = Col("File Count"), deleted = Col("Is Deleted"), template = Col("Root Web Template");
        string? Cell(List<string> cells, int index) => index >= 0 && index < cells.Count && !string.IsNullOrWhiteSpace(cells[index]) ? cells[index].Trim() : null;
        var urls = 0;
        foreach (var line in lines.Skip(1))
        {
            var cells = Csv(line);
            if (Cell(cells, url) is not { } siteUrl || !Uri.TryCreate(siteUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            urls++;
            var site = snapshot.Sites.FirstOrDefault(s => Same(s.Url, siteUrl));
            if (site is null)
            {
                if (string.Equals(Cell(cells, deleted), "true", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Contains("-my.sharepoint.com", StringComparison.OrdinalIgnoreCase)
                    || (scope is not null && !siteUrl.StartsWith(scope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var code = Cell(cells, template) ?? "";
                site = new SiteInfo
                {
                    Url = siteUrl.TrimEnd('/'),
                    Title = Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/')) is { Length: > 0 } name ? name : uri.Host,
                    Template = code,
                    Kind = code.Split('#') is [var web, var configuration] && int.TryParse(configuration, CultureInfo.InvariantCulture, out var c) ? KindOf(web, c) : KindOf(code, null),
                };
                snapshot.Sites.Add(site);
                reportOnly.Add(site.Url);
            }

            site.LastActivity = Date(Cell(cells, last)) ?? site.Created;
            site.StorageUsedMb = long.TryParse(Cell(cells, storage), CultureInfo.InvariantCulture, out var bytes) ? bytes / (1024 * 1024) : null;
            site.Content.Files = long.TryParse(Cell(cells, files), CultureInfo.InvariantCulture, out var count) ? count : null;
        }

        MarkHubs(snapshot);
        if (urls == 0 && snapshot.Sites.Count > 0)
        {
            snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Activity, "The usage report has no site URLs. Report concealment is probably on (Microsoft 365 admin center > Settings > Org settings > Reports)."));
            snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Sites, IncompleteSiteList("the usage report hides site URLs (report concealment)")));
        }
    }

    private async Task ReadSiteDetailAsync(SiteInfo site, bool reportOnly, ConcurrentBag<ScanIssue> issues, CancellationToken cancellationToken)
    {
        async Task Try(string area, Func<Task> read)
        {
            try
            {
                await read().ConfigureAwait(false);
            }
            catch (ConnectorException ex) when (ex.IsAccessDenied)
            {
                issues.Add(new ScanIssue(area, $"Access denied: {ex.Message}") { Target = site.Url });
            }
            catch (Exception ex) when (ex is ConnectorException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                issues.Add(new ScanIssue(area, ex.Message) { Target = site.Url });
            }
        }

        // The site itself first: access denied here means the person is not a member, and every other read would fail the same way.
        JsonNode? web = null;
        try
        {
            web = await http.GetAsync($"{site.Url}/_api/web?$select=Title,WebTemplate,Configuration,MegaMenuEnabled,Description,ServerRelativeUrl", cancellationToken, true).ConfigureAwait(false);
            site.Readable = true;
        }
        catch (ConnectorException ex) when (ex.IsAccessDenied)
        {
            site.Readable = false;
            issues.Add(new ScanIssue(ScanAreas.SiteDetail, "Access denied: you are not a member of this site, so its owners, navigation, content and permissions were not read. Hubwright never gives itself access; check it in the SharePoint admin center or with its owner.") { Target = site.Url });
            if (reportOnly && !site.IsHub)
            {
                issues.Add(new ScanIssue(ScanAreas.Hubs, HubNotRead) { Target = site.Url });
            }

            return;
        }
        catch (Exception ex) when (ex is ConnectorException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            issues.Add(new ScanIssue(ScanAreas.SiteDetail, ex.Message) { Target = site.Url });
        }

        if (reportOnly)
        {
            if (Text(web, "Title") is { Length: > 0 } title)
            {
                site.Title = title;
            }

            if (!site.IsHub)
            {
                await Try(ScanAreas.Hubs, async () =>
                {
                    var hub = Text(await http.GetAsync($"{site.Url}/_api/site?$select=HubSiteId", cancellationToken, true).ConfigureAwait(false), "HubSiteId")?.Trim('{', '}');
                    site.HubId = string.IsNullOrWhiteSpace(hub) || hub == Guid.Empty.ToString() ? null : hub;
                }).ConfigureAwait(false);
            }
        }

        await Try(ScanAreas.SiteDetail, async () =>
        {
            if (web is null)
            {
                return;
            }

            site.Template = $"{Text(web, "WebTemplate")}#{Int(web, "Configuration") ?? 0}";
            site.Kind = KindOf(Text(web, "WebTemplate") ?? "", Int(web, "Configuration"));
            site.MenuStyle = Bool(web, "MegaMenuEnabled") == true ? MenuStyle.Mega : MenuStyle.Cascading;
            var webs = await http.GetAsync($"{site.Url}/_api/web/webs?$select=Url", cancellationToken, true).ConfigureAwait(false);
            site.SubsiteCount = webs?["value"]?.AsArray().Count ?? 0;
            var owners = await http.GetAsync($"{site.Url}/_api/web/AssociatedOwnerGroup/Users?$select=Title", cancellationToken, true).ConfigureAwait(false);
            site.OwnerCount = owners?["value"]?.AsArray().Count;
        }).ConfigureAwait(false);

        await Try(ScanAreas.Navigation, async () =>
        {
            var which = site.Kind is SiteKind.Communication ? "TopNavigationBar" : "QuickLaunch";
            var nav = await http.GetAsync($"{site.Url}/_api/web/navigation/{which}?$expand=Children,Children/Children", cancellationToken, true).ConfigureAwait(false);
            site.Navigation = Nodes(nav?["value"]?.AsArray(), "Url", "Children");
            var titles = site.Navigation.Select(n => n.Title).ToList();
            site.Content.HasDefaultNavigation = site.Kind == SiteKind.Communication && titles.Count > 0
                && titles.TrueForAll(t => t is "Home" or "Documents" or "Pages" or "Site contents");
            site.Content.NavLinksToDocuments = CountDocumentLinks(site.Navigation);
        }).ConfigureAwait(false);

        await Try(ScanAreas.Content, async () =>
        {
            var lists = await http.GetAsync($"{site.Url}/_api/web/lists?$select=Title,BaseTemplate,BaseType,ItemCount,EnableVersioning,Hidden&$filter=Hidden eq false", cancellationToken, true).ConfigureAwait(false);
            var all = lists?["value"]?.AsArray().Where(l => l is not null).ToList() ?? [];
            site.Content.Libraries = all.Count(l => Int(l, "BaseType") == 1);
            site.Content.Lists = all.Count - site.Content.Libraries;
            site.Content.LargestListItems = all.Select(l => (long?)Int(l, "ItemCount")).Max() ?? 0;
            site.Content.LibrariesWithoutVersioning = all.Count(l => Int(l, "BaseType") == 1 && Bool(l, "EnableVersioning") == false);
            site.Content.UsesInfoPath = all.Exists(l => Int(l, "BaseTemplate") == 115);
        }).ConfigureAwait(false);

        await Try(ScanAreas.Content, async () =>
        {
            var path = new Uri(site.Url).AbsolutePath.TrimEnd('/');
            var pages = await http.GetAsync($"{site.Url}/_api/web/GetList('{path}/SitePages')/items?$select=PromotedState,FirstPublishedDate,Modified&$top=2000", cancellationToken, true).ConfigureAwait(false);
            var items = pages?["value"]?.AsArray().Where(p => p is not null).ToList() ?? [];
            site.Content.Pages = items.Count;
            site.Content.LastNewsPublished = items.Where(p => Int(p, "PromotedState") == 2).Select(p => Date(Text(p, "FirstPublishedDate"))).Max();
        }).ConfigureAwait(false);

        if (options.PermissionDepth >= 0)
        {
            await Try(ScanAreas.Permissions, async () =>
            {
                var roles = await http.GetAsync($"{site.Url}/_api/web/roleassignments?$expand=Member&$select=Member/LoginName", cancellationToken, true).ConfigureAwait(false);
                var logins = roles?["value"]?.AsArray().Select(r => Text(r?["Member"], "LoginName") ?? "").ToList() ?? [];
                site.Permissions.EeeuGrants = logins.Count(l => l.Contains("spo-grid-all-users", StringComparison.OrdinalIgnoreCase));
                site.Permissions.EveryoneGrants = logins.Count(l => l.StartsWith("c:0(.s|true", StringComparison.OrdinalIgnoreCase));
                site.IsPublic = site.Permissions.EeeuGrants > 0 || site.Permissions.EveryoneGrants > 0;
            }).ConfigureAwait(false);
        }
    }

    private async Task ReadHubNavigationAsync(HubInfo hub, CancellationToken cancellationToken)
    {
        var state = await http.GetAsync($"{hub.SiteUrl}/_api/navigation/MenuState?mapprovidername='GlobalNavigationSwitchableProvider'", cancellationToken, true).ConfigureAwait(false);
        hub.Navigation = Nodes(state?["Nodes"]?.AsArray(), "SimpleUrl", "Nodes");
    }

    private static List<NavNode> Nodes(JsonArray? array, string urlField, string childrenField)
        => [.. (array ?? []).Where(n => n is not null).Select(n => new NavNode
        {
            Title = Text(n, "Title") ?? "",
            Url = Text(n, urlField) is { Length: > 0 } url && url != "http://linkless.header/" ? url : null,
            AudienceTargeted = n!["AudienceIds"] is JsonArray audiences && audiences.Count > 0,
            Children = Nodes(n[childrenField] as JsonArray, urlField, childrenField),
        })];

    private static int CountDocumentLinks(IEnumerable<NavNode> nodes)
        => nodes.Sum(n => (n.Url is { } u && new[] { ".docx", ".pdf", ".xlsx", ".pptx", ".doc" }.Any(e => u.Split('?')[0].EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? 1 : 0) + CountDocumentLinks(n.Children));

    internal static SiteKind KindOf(string template, int? configuration) => template.ToUpperInvariant() switch
    {
        "SITEPAGEPUBLISHING" => SiteKind.Communication,
        "GROUP" => SiteKind.TeamGroup,
        "TEAMCHANNEL" => SiteKind.Channel,
        "STS" when configuration == 3 => SiteKind.TeamNoGroup,
        "STS" when configuration is null => SiteKind.Other,
        "STS" or "BLOG" or "WIKI" or "ENTERWIKI" or "SGS" or "SPS" or "SPSNEWS" or "CMSPUBLISHING" or "BLANKINTERNET" or "DEV" or "COMMUNITY" or "BDR" or "OFFILE" or "PROJECTSITE" => SiteKind.Classic,
        _ => SiteKind.Other,
    };

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonNode? node, string name) => node?[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    private static int? Int(JsonNode? node, string name)
        => node?[name] is JsonValue v ? v.TryGetValue<int>(out var i) ? i : int.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null : null;

    private static long? Long(JsonNode? node, string name)
        => node?[name] is JsonValue v && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static bool? Bool(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static DateTimeOffset? Date(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    internal static List<string> Csv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line.TrimEnd('\r'))
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
