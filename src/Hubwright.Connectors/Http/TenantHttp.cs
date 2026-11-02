using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Hubwright.Skills;

namespace Hubwright.Http;

/// <summary>A failed call, with the status so collectors can tell "no permission" from "broken".</summary>
public sealed class ConnectorException : Exception
{
    public ConnectorException()
    {
    }

    public ConnectorException(string message)
        : base(message)
    {
    }

    public ConnectorException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public ConnectorException(HttpStatusCode status, string message)
        : base(message)
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }

    public bool IsAccessDenied => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

/// <summary>One HttpClient for the process, with a Hubwright user agent.</summary>
public static class SharedHttp
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        // SharePoint asks tools to identify themselves; a decorated agent is throttled less aggressively.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NONISV|Hubwright|Hubwright/1.0");
        return client;
    }
}

/// <summary>
/// GET-only JSON over Graph and SharePoint REST with the signed-in person's token. Honours 429 and
/// 503 Retry-After as Microsoft's throttling guidance asks, and never sends a write.
/// </summary>
public sealed class TenantHttp(Func<Uri, CancellationToken, Task<string>> token, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? SharedHttp.Client;

    public int MaxRetries { get; init; } = 5;

    public async Task<JsonNode?> GetAsync(string url, CancellationToken cancellationToken, bool sharePointVerbose = false)
    {
        var uri = new Uri(url);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(uri, cancellationToken).ConfigureAwait(false));
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(sharePointVerbose ? "application/json;odata=nometadata" : "application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ConnectorException(response.StatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase} from {uri.Host}{uri.AbsolutePath}: {Trim(text)}");
            }

            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
    }

    /// <summary>
    /// The few SharePoint read endpoints that only answer POST (the theme manager). They take no
    /// body and change nothing; this is not a general POST.
    /// </summary>
    public async Task<JsonNode?> PostReadOnlyAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        if (!uri.AbsolutePath.Contains("/_api/thememanager/Get", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{uri.AbsolutePath} is not an allowed read-only POST endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent("", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(uri, cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json;odata=nometadata"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)
            : throw new ConnectorException(response.StatusCode, $"{(int)response.StatusCode} from {uri.Host}{uri.AbsolutePath}: {Trim(text)}");
    }

    /// <summary>Graph pages: follows @odata.nextLink up to <paramref name="max"/> items.</summary>
    public async IAsyncEnumerable<JsonNode> PagesAsync(string url, int max, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = 0;
        string? next = url;
        while (next is not null && count < max)
        {
            var page = await GetAsync(next, cancellationToken).ConfigureAwait(false);
            foreach (var item in page?["value"]?.AsArray() ?? [])
            {
                if (item is not null && count++ < max)
                {
                    yield return item;
                }
            }

            next = page?["@odata.nextLink"]?.GetValue<string>();
        }
    }

    public async Task<string> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(uri, cancellationToken).ConfigureAwait(false));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? text : throw new ConnectorException(response.StatusCode, $"{(int)response.StatusCode} from {uri.Host}{uri.AbsolutePath}");
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
}

/// <summary>Fetches public documentation pages for skill refreshes. No sign-in, no cookies.</summary>
public sealed class HttpSourceFetcher(HttpClient? http = null) : ISourceFetcher
{
    private readonly HttpClient _http = http ?? SharedHttp.Client;

    public async Task<(string? Text, string? Error)> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return (Encoding.UTF8.GetString(bytes), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, ex.Message);
        }
    }
}
