using Hubwright.Http;

namespace Hubwright.Auth;

/// <summary>Finds the tenant's SharePoint address from Graph after sign-in, so nobody has to type it.</summary>
public static class TenantDiscovery
{
    public const string RootSiteUrl = "https://graph.microsoft.com/v1.0/sites/root?$select=webUrl";

    /// <summary>The prefix in <c>https://contoso.sharepoint.com</c>, or null for anything that isn't a SharePoint Online root.</summary>
    public static string? PrefixFromUrl(string? webUrl)
    {
        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        const string suffix = ".sharepoint.com";
        var host = uri.Host;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prefix = host[..^suffix.Length];
        return prefix.Length == 0 || prefix.Contains('.', StringComparison.Ordinal) || prefix.EndsWith("-admin", StringComparison.OrdinalIgnoreCase) || prefix.EndsWith("-my", StringComparison.OrdinalIgnoreCase)
            ? null
            : prefix.ToLowerInvariant();
    }

    public static async Task<string> FindPrefixAsync(TenantAuth auth, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var root = await new TenantHttp(auth.TokenForAsync).GetAsync(RootSiteUrl, cancellationToken).ConfigureAwait(false);
        var url = root?["webUrl"]?.GetValue<string>();
        return PrefixFromUrl(url)
            ?? throw new ConnectorException($"Graph returned '{url}' as the root site, which is not a SharePoint Online address. Enter the tenant prefix yourself.");
    }
}
