using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Hubwright.Runtime;
using Hubwright.Storage;

namespace Hubwright.Auth;

/// <summary>What Hubwright calls, each with its own token.</summary>
public enum TenantResource
{
    /// <summary>Microsoft Graph: sites, groups, reports, labels.</summary>
    Graph,

    /// <summary>SharePoint REST on the tenant's sites.</summary>
    SharePoint,

    /// <summary>SharePoint REST on the admin host, for tenant settings (needs the SharePoint Administrator role).</summary>
    SharePointAdmin,
}

public sealed record SignedInAccount(string Username, string TenantId);

/// <summary>Which of the app's permissions a sign-in asks for. Both use the same Hubwright app.</summary>
public enum AccessProfile
{
    /// <summary>Read scopes only: scans, assessments, proposals.</summary>
    ReadOnly,

    /// <summary>The write scopes the Implement page needs, consented on top of the read ones (incremental consent).</summary>
    Implement,
}

/// <summary>A device-code sign-in waiting for the person: enter <paramref name="UserCode"/> at <paramref name="Url"/>.</summary>
public sealed record DeviceCodePrompt(string UserCode, string Url, string Message);

/// <summary>
/// Delegated sign-in as the admin running Hubwright, read-only by design. Hubwright sees exactly what
/// the signed-in person can see. The person picks how (<see cref="SignInMethod"/>): the system
/// browser (the default), the Windows account broker, or a device code. Tokens are cached in
/// <c>~/.hubwright/msal.cache</c>, encrypted on Windows, so the CLI, the desktop app and the MCP
/// server share one sign-in.
/// </summary>
public sealed partial class TenantAuth
{
    /// <summary>Read-only Graph permissions; documented in docs/permissions.md.</summary>
    public static readonly string[] GraphScopes =
    [
        "https://graph.microsoft.com/Sites.Read.All",
        "https://graph.microsoft.com/Group.Read.All",
        "https://graph.microsoft.com/User.Read.All",
        "https://graph.microsoft.com/Reports.Read.All",
    ];

    private const string BrowserDone = """
        <html><head><title>Hubwright</title></head>
        <body style="font-family:Segoe UI,system-ui,sans-serif;display:grid;place-items:center;height:90vh;color:#323130">
        <div style="text-align:center"><div style="font-size:48px">🏗️</div><h2>You're signed in to Hubwright</h2>
        <p>You can close this tab and go back to Hubwright. Signing in changes nothing in your tenant.</p></div></body></html>
        """;

    /// <summary>Graph scopes for implementing: tenant sharing settings, and reading sites.</summary>
    public static readonly string[] ImplementGraphScopes =
    [
        "https://graph.microsoft.com/SharePointTenantSettings.ReadWrite.All",
        "https://graph.microsoft.com/Sites.Read.All",
    ];

    private readonly TenantConnection _tenant;
    private readonly Func<IntPtr>? _window;
    private readonly Lazy<Task<IPublicClientApplication>> _app;
    private readonly string _clientId;

    public TenantAuth(TenantConnection tenant, Func<IntPtr>? window = null, AccessProfile profile = AccessProfile.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var clientId = tenant.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("No app registration yet. Run tools/register-app.ps1 (Connection → Register the app), or `hubwright connect --client-id <app id>`.");
        }

        _tenant = tenant;
        _window = window;
        _clientId = clientId;
        Profile = profile;
        _app = new Lazy<Task<IPublicClientApplication>>(BuildAsync);
    }

    public SignInMethod Method => _tenant.SignIn;

    public AccessProfile Profile { get; }

    public string[] ScopesFor(TenantResource resource) => (resource, Profile) switch
    {
        (TenantResource.Graph, AccessProfile.Implement) => ImplementGraphScopes,
        (TenantResource.Graph, _) => GraphScopes,
        (TenantResource.SharePoint, AccessProfile.Implement) => [$"{RequireName().RootUrl}/AllSites.FullControl"],
        (TenantResource.SharePoint, _) => [$"{RequireName().RootUrl}/AllSites.Read"],
        (TenantResource.SharePointAdmin, _) => [$"{RequireName().AdminUrl}/.default"],
        _ => throw new ArgumentOutOfRangeException(nameof(resource)),
    };

    public async Task<SignedInAccount?> CurrentAsync()
    {
        var app = await _app.Value.ConfigureAwait(false);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        return account is null ? null : new SignedInAccount(account.Username, account.HomeAccountId?.TenantId ?? "");
    }

    /// <summary>
    /// Signs in for Graph. SharePoint is consented separately (<see cref="EnsureSharePointAsync"/>)
    /// because the SharePoint host is only known once Graph has told us the tenant's root site.
    /// </summary>
    public async Task<SignedInAccount> SignInAsync(Func<DeviceCodePrompt, Task>? deviceCode = null, CancellationToken cancellationToken = default)
    {
        var result = await InteractiveAsync(ScopesFor(TenantResource.Graph), deviceCode, cancellationToken).ConfigureAwait(false);
        return new SignedInAccount(result.Account.Username, result.TenantId);
    }

    /// <summary>A SharePoint token silently, or one more prompt when SharePoint has not been consented yet.</summary>
    public async Task EnsureSharePointAsync(Func<DeviceCodePrompt, Task>? deviceCode = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await TokenAsync(TenantResource.SharePoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SignInRequiredException)
        {
            await InteractiveAsync(ScopesFor(TenantResource.SharePoint), deviceCode, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SignOutAsync()
    {
        var app = await _app.Value.ConfigureAwait(false);
        foreach (var account in await app.GetAccountsAsync().ConfigureAwait(false))
        {
            await app.RemoveAsync(account).ConfigureAwait(false);
        }
    }

    /// <summary>A token, silently; throws <see cref="SignInRequiredException"/> when the person has to sign in.</summary>
    public async Task<string> TokenAsync(TenantResource resource, CancellationToken cancellationToken = default)
    {
        var app = await _app.Value.ConfigureAwait(false);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
            ?? throw new SignInRequiredException("Nobody is signed in. Sign in first (`hubwright login`, or Connection in the app).");
        try
        {
            var result = await app.AcquireTokenSilent(ScopesFor(resource), account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new SignInRequiredException($"Sign in again ({ex.Classification}): `hubwright login`, or Connection in the app.", ex);
        }
    }

    /// <summary>The right token for a URL: Graph, the admin host, or the tenant's sites.</summary>
    public Task<string> TokenForAsync(Uri url, CancellationToken cancellationToken)
        => TokenAsync(
            url.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ? TenantResource.Graph
            : url.Host.EndsWith("-admin.sharepoint.com", StringComparison.OrdinalIgnoreCase) ? TenantResource.SharePointAdmin
            : TenantResource.SharePoint,
            cancellationToken);

    private async Task<AuthenticationResult> InteractiveAsync(string[] scopes, Func<DeviceCodePrompt, Task>? deviceCode, CancellationToken cancellationToken)
    {
        var app = await _app.Value.ConfigureAwait(false);
        if (_tenant.SignIn == SignInMethod.DeviceCode)
        {
            return await app.AcquireTokenWithDeviceCode(scopes, async code =>
            {
                var prompt = new DeviceCodePrompt(code.UserCode, code.VerificationUrl, code.Message);
                if (deviceCode is null)
                {
                    Console.Error.WriteLine(prompt.Message);
                }
                else
                {
                    await deviceCode(prompt).ConfigureAwait(false);
                }
            }).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }

        var request = app.AcquireTokenInteractive(scopes).WithPrompt(Prompt.SelectAccount);
        if (_tenant.SignIn == SignInMethod.Browser)
        {
            request = request
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = BrowserDone });
        }

        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    private TenantConnection RequireName()
        => string.IsNullOrWhiteSpace(_tenant.Name)
            ? throw new InvalidOperationException("The tenant's SharePoint address is not known yet. Sign in first; Hubwright finds it from Graph.")
            : _tenant;

    private async Task<IPublicClientApplication> BuildAsync()
    {
        var builder = PublicClientApplicationBuilder.Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, string.IsNullOrWhiteSpace(_tenant.TenantId) ? "organizations" : _tenant.TenantId)
            .WithRedirectUri("http://localhost")
            .WithClientName("Hubwright");

        if (OperatingSystem.IsWindows() && _tenant.SignIn == SignInMethod.Windows)
        {
            builder = builder
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Hubwright" })
                .WithParentActivityOrWindow(_window ?? ConsoleWindow);
        }

        var app = builder.Build();
        var storage = OperatingSystem.IsWindows()
            ? new StorageCreationPropertiesBuilder("msal.cache", HubwrightHome.Directory).Build()
            : new StorageCreationPropertiesBuilder("msal.cache", HubwrightHome.Directory).WithUnprotectedFile().Build();
        var cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        cache.RegisterCache(app.UserTokenCache);
        return app;
    }

    private static IntPtr ConsoleWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var console = GetConsoleWindow();
        var owner = GetAncestor(console, 3); // GA_ROOTOWNER
        return owner != IntPtr.Zero ? owner : console;
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetConsoleWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);
}

public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException()
    {
    }

    public SignInRequiredException(string message)
        : base(message)
    {
    }

    public SignInRequiredException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
