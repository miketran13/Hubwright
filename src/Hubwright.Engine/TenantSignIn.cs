using Microsoft.Identity.Client;
using Hubwright.Auth;
using Hubwright.Storage;

namespace Hubwright.Engine;

/// <param name="Discovered">The SharePoint prefix Graph reported; differs from <paramref name="Typed"/> when the typed one was wrong.</param>
public sealed record SignInOutcome(SignedInAccount Account, string Tenant, string? Typed, string Discovered)
{
    public bool Corrected => Typed is not null && !string.Equals(Typed, Discovered, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The whole sign-in, the same for the CLI and the desktop app: sign in for Graph (browser, Windows
/// broker or device code), ask Graph for the root site to learn the SharePoint address, then make
/// sure SharePoint is consented. The connection is saved to hubwright.json; tokens stay in the cache.
/// </summary>
public static class TenantSignIn
{
    /// <summary>
    /// Grants the Hubwright app its write scopes for this person (incremental consent on the same app),
    /// for the tenant the read-only sign-in already found. Silent when they are already consented.
    /// Used only by the Implement page and <c>hubwright implement</c>.
    /// </summary>
    public static async Task<SignedInAccount> ImplementAsync(HubwrightConfig config, Func<IntPtr>? window, Func<DeviceCodePrompt, Task>? deviceCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Tenant.Name))
        {
            throw new InvalidOperationException("Sign in first (Connection, or `hubwright login`), so Hubwright knows the tenant.");
        }

        var auth = new TenantAuth(config.Tenant, window, AccessProfile.Implement);
        var account = await auth.CurrentAsync().ConfigureAwait(false);
        try
        {
            await auth.TokenAsync(TenantResource.Graph, cancellationToken).ConfigureAwait(false);
        }
        catch (SignInRequiredException)
        {
            account = await auth.SignInAsync(deviceCode, cancellationToken).ConfigureAwait(false);
        }

        await auth.EnsureSharePointAsync(deviceCode, cancellationToken).ConfigureAwait(false);
        account ??= await auth.CurrentAsync().ConfigureAwait(false) ?? throw new SignInRequiredException("Sign-in did not complete.");
        new AuditLog().Append("login.implement", $"{account.Username} → {config.Tenant.Name}.sharepoint.com (write scopes)");
        return account;
    }

    public static async Task<SignInOutcome> RunAsync(HubwrightConfig config, Func<IntPtr>? window, Func<DeviceCodePrompt, Task>? deviceCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        var typed = string.IsNullOrWhiteSpace(config.Tenant.Name) ? null : TenantStore.Clean(config.Tenant.Name);
        var auth = new TenantAuth(config.Tenant, window);
        SignedInAccount account;
        try
        {
            account = await auth.SignInAsync(deviceCode, cancellationToken).ConfigureAwait(false);
        }
        catch (MsalServiceException ex) when (string.IsNullOrWhiteSpace(config.Tenant.TenantId) && (ex.Message.Contains("AADSTS50059", StringComparison.Ordinal) || ex.Message.Contains("AADSTS50194", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Entra ID needs to know your directory: Hubwright's app is registered for your organization only. Enter the directory (tenant) id or domain, for example contoso.onmicrosoft.com (`hubwright connect --tenant-id …`, or the Connection page).", ex);
        }

        if (string.IsNullOrWhiteSpace(config.Tenant.TenantId) && !string.IsNullOrWhiteSpace(account.TenantId))
        {
            config.Tenant.TenantId = account.TenantId;
        }

        var discovered = await TenantDiscovery.FindPrefixAsync(auth, cancellationToken).ConfigureAwait(false);
        config.Tenant.Name = discovered;
        await auth.EnsureSharePointAsync(deviceCode, cancellationToken).ConfigureAwait(false);
        config.Save();
        new AuditLog().Append("login", $"{account.Username} → {discovered}.sharepoint.com ({config.Tenant.SignIn})");
        return new SignInOutcome(account, discovered, typed, discovered);
    }
}
