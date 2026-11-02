using System.Collections;
using System.Reflection;
using Hubwright.Snapshot;

namespace Hubwright.Rules;

/// <summary>What a rule looks at: the tenant once, every site, or every hub.</summary>
public enum RuleScope { Tenant, Site, Hub }

/// <summary>One fact a rule may read, with what it means (the catalog behind docs/rules-dsl.md).</summary>
public sealed record FactDefinition(string Name, string Description);

/// <summary>
/// The facts rules read, per scope. Snapshot properties appear under their JSON names
/// (<c>ownerCount</c>, <c>permissions.eeeuGrants</c>, <c>settings.sharingLevel</c>); derived facts
/// (navigation depth, days since activity, hub depth) are computed here, once, so skills combine
/// them without code.
/// </summary>
public static class FactCatalog
{
    private static readonly Dictionary<RuleScope, Dictionary<string, Derived>> DerivedFacts = new()
    {
        [RuleScope.Tenant] = Map(
            D("siteCount", "Sites in the snapshot (unknown when the site list is incomplete).", c => SiteList(c, s => s.Count)),
            D("hubCount", "Hub sites.", c => c.Snapshot.Hubs.Count),
            D("homeSiteSet", "Whether a home site is set.", c => c.Snapshot.Sites.Count == 0 || Missing(c, ScanAreas.HomeSite) ? null : c.Snapshot.HomeSite is not null),
            D("homeSiteIsHub", "Whether the home site is also a hub.", c => c.Snapshot.HomeSite?.IsHub),
            D("classicSiteCount", "Sites built on a classic template (unknown when the site list is incomplete).", c => SiteList(c, s => s.Count(x => x.Kind == SiteKind.Classic))),
            D("sitesWithSubsites", "Sites that have at least one subsite.", c => KnownCount(c.Snapshot.Sites, s => s.SubsiteCount, v => v > 0)),
            D("sitesWithoutHub", "Sites (not hubs, not the home site) associated with no hub; sites whose hub was not read are left out.", c => SiteList(c, s => s.Count(x => !x.IsHub && !x.IsHomeSite && x.HubId is null && !c.Snapshot.Unread(ScanAreas.Hubs, x.Url)))),
            D("unreadableSites", "Sites the person who scanned could not read (not a member), so their details are not assessed.", c => c.Snapshot.Sites.Count(s => s.Readable == false)),
            D("ownerlessSites", "Sites with no owner.", c => KnownCount(c.Snapshot.Sites, s => s.OwnerCount, v => v == 0)),
            D("customThemeCount", "Custom themes the tenant offers.", c => c.Snapshot.Themes.Count),
            D("distinctHubThemes", "Different themes used across hubs.", c => c.Snapshot.Hubs.Select(h => h.ThemeName).Where(t => t is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count()),
            D("maxHubDepth", "Deepest hub-to-hub association chain (1 = a hub with no parent hub).", c => c.Snapshot.Hubs.Count == 0 ? 0 : c.Snapshot.Hubs.Max(h => HubDepth(c.Snapshot, h))),
            D("globalNavLinks", "Links in global navigation (the app bar).", c => Missing(c, ScanAreas.GlobalNavigation) ? null : Navigation.Count(c.Snapshot.GlobalNavigation)),
            D("hubsInGlobalNav", "Hubs linked from global navigation.", c => Missing(c, ScanAreas.GlobalNavigation) ? null : c.Snapshot.Hubs.Count(h => Navigation.Links(c.Snapshot.GlobalNavigation, h.SiteUrl))),
            D("storageUsedPercent", "Tenant storage used, percent of the quota.", c => c.Snapshot.Settings is { StorageQuotaMb: > 0, StorageUsedMb: { } used } s ? used * 100.0 / s.StorageQuotaMb!.Value : null),
            D("unlabeledSites", "Sites without a sensitivity label (unknown when labels were not read).", c => c.Snapshot.ScanIssues.Exists(i => i.Area == ScanAreas.Labels) ? null : c.Snapshot.Sites.Count(s => s.SensitivityLabel is null)),
            D("classicThemeSites", "Sites still on a classic theme.", c => KnownCount(c.Snapshot.Sites, s => s.UsesClassicTheme, v => v)),
            D("hubNamesConsistent", "Whether every hub title shares one naming pattern (same suffix or prefix word).", c => HubNamesConsistent(c.Snapshot)),
            D("themesFailingContrast", "Custom themes with a text/background pair below WCAG 2.1 AA.", c => c.Snapshot.Themes.Count(t => !Theming.ThemeGenerator.Check(t.Palette).All(x => x.Passes)))),
        [RuleScope.Site] = Map(
            D("daysSinceActivity", "Days since the last activity on the site.", c => DaysSince(c.Site!.LastActivity)),
            D("daysSinceNews", "Days since the last news post.", c => DaysSince(c.Site!.Content.LastNewsPublished)),
            D("inHub", "Whether the site is associated with a hub (unknown when its hub was not read).", c => c.Snapshot.Unread(ScanAreas.Hubs, c.Site!.Url) ? null : c.Site.HubId is not null),
            D("hubTitle", "The title of the site's hub.", c => c.Snapshot.Hubs.FirstOrDefault(h => h.Id == c.Site!.HubId)?.Title),
            D("navLinks", "Links in the site's own navigation, all levels.", c => SiteNav(c, Navigation.Count)),
            D("navMaxChildren", "The most links at any one level of site navigation.", c => SiteNav(c, n => Navigation.MaxChildren(n))),
            D("navDepth", "Levels in site navigation.", c => SiteNav(c, Navigation.Depth)),
            D("navEmptyLabels", "Navigation labels with no link below them.", c => SiteNav(c, Navigation.EmptyLabels)),
            D("navRestrictedUntargeted", "Links to restricted sites shown to everyone (no audience targeting).", c => SiteNav(c, Navigation.RestrictedUntargeted)),
            D("sharingLooserThanTenant", "Whether the site's sharing is more permissive than the tenant's.", c => c.Site!.SharingLevel is { } site && c.Snapshot.Settings.SharingLevel is { } tenant ? site < tenant : null),
            D("tenantDefaultLinkType", "The tenant's default sharing link type.", c => c.Snapshot.Settings.DefaultLinkType)),
        [RuleScope.Hub] = Map(
            D("associatedSites", "Sites associated with this hub.", c => c.Snapshot.Sites.Count(s => s.HubId == c.Hub!.Id && !string.Equals(s.Url, c.Hub.SiteUrl, StringComparison.OrdinalIgnoreCase))),
            D("depth", "Position in the hub-to-hub chain (1 = no parent hub).", c => HubDepth(c.Snapshot, c.Hub!)),
            D("navLinks", "Links in hub navigation, all levels.", c => HubNav(c, Navigation.Count)),
            D("navMaxChildren", "The most links at any one level of hub navigation.", c => HubNav(c, n => Navigation.MaxChildren(n))),
            D("navDepth", "Levels in hub navigation.", c => HubNav(c, Navigation.Depth)),
            D("navEmptyLabels", "Hub navigation labels with no link below them.", c => HubNav(c, Navigation.EmptyLabels)),
            D("navRestrictedUntargeted", "Hub links to restricted sites shown to everyone.", c => HubNav(c, Navigation.RestrictedUntargeted)),
            D("extranetSitesAssociated", "Extranet sites associated with this hub.", c => c.Snapshot.Sites.Count(s => s.HubId == c.Hub!.Id && s.IsExtranet)),
            D("onlyPurposeIsTheme", "Whether the hub has associated sites but no navigation (it only shares a theme).", c => Missing(c, ScanAreas.Navigation, c.Hub!.SiteUrl) ? null : c.Snapshot.Sites.Any(s => s.HubId == c.Hub.Id) && c.Hub.Navigation.Count == 0)),
    };

    private static readonly Dictionary<RuleScope, Dictionary<string, PropertyPath>> Properties = new()
    {
        [RuleScope.Tenant] = Reflect(typeof(TenantSnapshot), "", nameof(TenantSnapshot.Settings)),
        [RuleScope.Site] = Reflect(typeof(SiteInfo), "", nameof(SiteInfo.Permissions), nameof(SiteInfo.Content)),
        [RuleScope.Hub] = Reflect(typeof(HubInfo), ""),
    };

    /// <summary>Every fact a rule of this scope can read.</summary>
    public static IReadOnlyList<FactDefinition> For(RuleScope scope)
        => [.. Properties[scope].Values.Select(p => new FactDefinition(p.Name, p.Description)).Concat(DerivedFacts[scope].Values.Select(d => d.Definition)).OrderBy(f => f.Name, StringComparer.Ordinal)];

    public static bool Exists(RuleScope scope, string name)
        => Properties[scope].ContainsKey(name) || DerivedFacts[scope].ContainsKey(name);

    internal static object? Resolve(RuleScope scope, FactContext context, string name)
    {
        if (DerivedFacts[scope].TryGetValue(name, out var derived))
        {
            return derived.Get(context) ?? Expression.Unknown;
        }

        if (Properties[scope].TryGetValue(name, out var property))
        {
            object target = scope switch
            {
                RuleScope.Tenant => context.Snapshot,
                RuleScope.Site => context.Site!,
                _ => context.Hub!,
            };
            return property.Get(target) ?? Expression.Unknown;
        }

        throw new RuleException($"There is no fact '{name}' for {scope.ToString().ToLowerInvariant()} rules. Run `hubwright facts {scope.ToString().ToLowerInvariant()}` to list them.");
    }

    private static bool Missing(FactContext c, string area, string? target = null)
        => c.Snapshot.ScanIssues.Exists(i => i.Area == area && (target is null || i.Target is null || string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)));

    private static object? SiteList(FactContext c, Func<List<SiteInfo>, int> measure)
        => c.Snapshot.SiteListIncomplete ? null : measure(c.Snapshot.Sites);

    private static object? SiteNav(FactContext c, Func<List<NavNode>, int> measure)
        => Missing(c, ScanAreas.Navigation, c.Site!.Url) || Missing(c, ScanAreas.SiteDetail, c.Site.Url) ? null : measure(c.Site.Navigation);

    private static object? HubNav(FactContext c, Func<List<NavNode>, int> measure)
        => Missing(c, ScanAreas.Navigation, c.Hub!.SiteUrl) ? null : measure(c.Hub.Navigation);

    private static double? DaysSince(DateTimeOffset? when) => when is { } value ? Math.Floor((Values.Now - value).TotalDays) : null;

    private static int? KnownCount<T>(IEnumerable<SiteInfo> sites, Func<SiteInfo, T?> value, Func<T, bool> predicate)
        where T : struct
    {
        var known = sites.Select(value).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return known.Count == 0 && sites.Any() ? null : known.Count(predicate);
    }

    internal static int HubDepth(TenantSnapshot snapshot, HubInfo hub)
    {
        var depth = 1;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { hub.Id };
        var current = hub;
        while (current.ParentHubId is { } parentId && snapshot.Hubs.FirstOrDefault(h => h.Id == parentId) is { } parent && seen.Add(parent.Id))
        {
            depth++;
            current = parent;
        }

        return depth;
    }

    private static bool? HubNamesConsistent(TenantSnapshot snapshot)
    {
        if (snapshot.Hubs.Count < 2)
        {
            return null;
        }

        var words = snapshot.Hubs.Select(h => h.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 0).ToList();
        var sameLast = words.Select(w => w[^1]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        var sameFirst = words.Select(w => w[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        return sameLast || sameFirst;
    }

    private sealed record Derived(FactDefinition Definition, Func<FactContext, object?> Get);

    private static Derived D(string name, string description, Func<FactContext, object?> get) => new(new(name, description), get);

    private static Dictionary<string, Derived> Map(params Derived[] facts)
        => facts.ToDictionary(f => f.Definition.Name, StringComparer.OrdinalIgnoreCase);

    private sealed record PropertyPath(string Name, string Description, PropertyInfo[] Chain)
    {
        public object? Get(object target)
        {
            object? value = target;
            foreach (var property in Chain)
            {
                value = value is null ? null : property.GetValue(value);
            }

            return value;
        }
    }

    private static Dictionary<string, PropertyPath> Reflect(Type type, string prefix, params string[] nested)
    {
        var map = new Dictionary<string, PropertyPath>(StringComparer.OrdinalIgnoreCase);
        Add(map, type, prefix, [], nested);
        return map;
    }

    private static void Add(Dictionary<string, PropertyPath> map, Type type, string prefix, PropertyInfo[] chain, string[] nested)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not null)
            {
                continue;
            }

            var name = prefix + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            var path = (PropertyInfo[])[.. chain, property];
            if (nested.Contains(property.Name))
            {
                Add(map, property.PropertyType, name + ".", path, []);
                continue;
            }

            var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var isScalar = underlying.IsPrimitive || underlying.IsEnum || underlying == typeof(string) || underlying == typeof(DateTimeOffset) || underlying == typeof(decimal);
            var isList = typeof(IEnumerable).IsAssignableFrom(underlying) && underlying != typeof(string);
            if (isScalar || isList)
            {
                var description = underlying.IsEnum
                    ? $"One of: {string.Join(", ", Enum.GetNames(underlying))}."
                    : isList ? "A list; use count()." : underlying.Name;
                map[name] = new PropertyPath(name, description, path);
            }
        }
    }
}

/// <summary>The snapshot and the one site or hub a rule is looking at.</summary>
public sealed record FactContext(TenantSnapshot Snapshot, SiteInfo? Site = null, HubInfo? Hub = null);

/// <summary>Facts for one rule target, with the rule's parameters (after tenant overrides).</summary>
public sealed class SnapshotFacts(RuleScope scope, FactContext context, IReadOnlyDictionary<string, object?> parameters) : IFactSource
{
    public object? Get(string name) => FactCatalog.Resolve(scope, context, name);

    public object? Parameter(string name)
        => parameters.TryGetValue(name, out var value)
            ? value
            : throw new RuleException($"The rule uses ${name} but has no parameter '{name}'. Add it under params.");
}

/// <summary>Navigation tree measures shared by site, hub and global navigation.</summary>
public static class Navigation
{
    public static int Count(IEnumerable<NavNode> nodes) => nodes.Sum(n => 1 + Count(n.Children));

    public static int Depth(IEnumerable<NavNode> nodes) => nodes.Select(n => 1 + Depth(n.Children)).DefaultIfEmpty(0).Max();

    public static int MaxChildren(IReadOnlyCollection<NavNode> nodes)
        => Math.Max(nodes.Count, nodes.Select(n => MaxChildren(n.Children)).DefaultIfEmpty(0).Max());

    public static int EmptyLabels(IEnumerable<NavNode> nodes)
        => nodes.Sum(n => (n.IsLabel && !n.Children.Exists(c => !c.IsLabel || c.Children.Count > 0) ? 1 : 0) + EmptyLabels(n.Children));

    public static int RestrictedUntargeted(IEnumerable<NavNode> nodes)
        => nodes.Sum(n => (n.Restricted && !n.AudienceTargeted ? 1 : 0) + RestrictedUntargeted(n.Children));

    public static bool Links(IEnumerable<NavNode> nodes, string url)
        => nodes.Any(n => string.Equals(n.Url?.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) || Links(n.Children, url));
}
