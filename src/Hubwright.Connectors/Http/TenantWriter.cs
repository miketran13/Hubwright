using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hubwright.Http;

/// <summary>A write Hubwright is about to send, or would send in a dry run.</summary>
public sealed record WriteRequest(string Method, string Url, string? Body, bool Merge);

/// <summary>
/// The only place Hubwright writes to a tenant. It sends a closed list of documented requests
/// (site creation, hub registration and association, hub rename, tenant themes, tenant sharing)
/// and refuses anything else, including every DELETE. Each request is reported to <see cref="Log"/>
/// before it is sent and again with its result, so the audit log holds every write. In a dry run
/// nothing is sent: requests are only recorded.
/// </summary>
public sealed partial class TenantWriter(Func<Uri, CancellationToken, Task<string>> token, HttpClient? http = null)
{
    private const string Guid = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";
    private const string SitePath = "(/(sites|teams)/[^/?#]+)?";

    private static readonly (string Method, Regex Path, string Why)[] Allowed =
    [
        ("POST", new Regex("^/_api/SPSiteManager/create$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "create a site"),
        ("POST", new Regex($"^{SitePath}/_api/site/RegisterHubSite$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "register a hub"),
        ("POST", new Regex($"^{SitePath}/_api/site/JoinHubSite\\('{Guid}'\\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "associate a site with a hub"),
        ("POST", new Regex("^/_api/HubSites/GetById$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "update a hub's display properties"),
        ("POST", new Regex($"^{SitePath}/_api/thememanager/(AddTenantTheme|UpdateTenantTheme|ApplyTheme)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "add, update or apply a theme"),
        ("POST", new Regex($"^{SitePath}/_api/contextinfo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "get a request digest"),
        ("PATCH", new Regex("^/v1\\.0/admin/sharepoint/settings$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "update tenant sharing settings"),
    ];

    /// <summary>What each allow-listed request does, for the scope and permissions summary.</summary>
    public static IReadOnlyList<string> Operations => [.. Allowed.Select(a => a.Why).Where(w => w != "get a request digest")];

    /// <summary>
    /// The only Graph sharepointSettings properties Hubwright changes: the tenant sharing level and the
    /// security baseline's documented settings (learn.microsoft.com/graph/api/sharepointsettings-update).
    /// </summary>
    public static IReadOnlyList<string> GraphSettings => Security.SecurityBaseline.GraphProperties;

    private readonly HttpClient _http = http ?? SharedHttp.Client;

    public bool DryRun { get; init; }

    /// <summary>Called before a write is sent (status null) and after (status and a short result).</summary>
    public Action<WriteRequest, int?, string?>? Log { get; init; }

    public List<WriteRequest> Recorded { get; } = [];

    public int MaxRetries { get; init; } = 5;

    /// <summary>Whether Hubwright may send this request at all.</summary>
    public static bool IsAllowed(string method, Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = url.Host.ToLowerInvariant();
        var graph = host == "graph.microsoft.com";
        if (!graph && !host.EndsWith(".sharepoint.com", StringComparison.Ordinal))
        {
            return false;
        }

        if (url.AbsolutePath.Contains("/_api/HubSites/GetById", StringComparison.OrdinalIgnoreCase) && !HubQuery().IsMatch(url.Query))
        {
            return false;
        }

        return Allowed.Any(a => a.Method.Equals(method, StringComparison.OrdinalIgnoreCase) && a.Path.IsMatch(url.AbsolutePath) && (a.Method == "PATCH") == graph);
    }

    /// <summary>Sends one allowed write; returns the response JSON (null for 204, or in a dry run).</summary>
    public async Task<JsonNode?> SendAsync(string method, string url, object? body, CancellationToken cancellationToken, bool merge = false)
    {
        var uri = new Uri(url);
        if (!IsAllowed(method, uri))
        {
            throw new InvalidOperationException($"Hubwright does not send {method} {uri.Host}{uri.AbsolutePath}: it is not one of its documented, allow-listed writes.");
        }

        var json = body is null ? null : body as string ?? JsonSerializer.Serialize(body);
        if (uri.Host == "graph.microsoft.com" && (json is null || JsonNode.Parse(json) is not JsonObject properties || properties.Count == 0 || properties.Any(p => !GraphSettings.Contains(p.Key, StringComparer.Ordinal))))
        {
            throw new InvalidOperationException($"Hubwright changes only these tenant settings: {string.Join(", ", GraphSettings)}.");
        }

        var write = new WriteRequest(method, url, json, merge);
        Recorded.Add(write);
        if (DryRun)
        {
            return null;
        }

        Log?.Invoke(write, null, null);
        string? digest = null;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(uri, cancellationToken).ConfigureAwait(false));
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(uri.Host == "graph.microsoft.com" ? "application/json" : "application/json;odata=nometadata"));
            if (merge)
            {
                request.Headers.Add("X-HTTP-Method", "MERGE");
                request.Headers.TryAddWithoutValidation("IF-MATCH", "*");
            }

            if (digest is not null)
            {
                request.Headers.Add("X-RequestDigest", digest);
            }

            request.Content = new StringContent(json ?? "", Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // OAuth requests normally need no form digest; ask for one only if SharePoint insists.
            if (!response.IsSuccessStatusCode && digest is null && text.Contains("digest", StringComparison.OrdinalIgnoreCase) && uri.Host != "graph.microsoft.com")
            {
                digest = await DigestAsync(uri, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Log?.Invoke(write, (int)response.StatusCode, Trim(text));
            if (!response.IsSuccessStatusCode)
            {
                throw new ConnectorException(response.StatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase} from {method} {uri.Host}{uri.AbsolutePath}: {Trim(text)}");
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    private async Task<string> DigestAsync(Uri target, CancellationToken cancellationToken)
    {
        var site = SiteRoot(target);
        var uri = new Uri(site + "/_api/contextinfo");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(uri, cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata=nometadata"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return node?["FormDigestValue"]?.GetValue<string>() ?? throw new ConnectorException(response.StatusCode, "SharePoint asked for a request digest and did not return one.");
    }

    /// <summary>The site a URL belongs to: https://host or https://host/sites/name.</summary>
    public static string SiteRoot(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var match = SiteRootPath().Match(url.AbsolutePath);
        return $"https://{url.Host}{(match.Success ? match.Value : "")}";
    }

    [GeneratedRegex($"^hubSiteId='{Guid}'$|^\\?hubSiteId='{Guid}'$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HubQuery();

    [GeneratedRegex("^/(sites|teams)/[^/?#]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SiteRootPath();

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
