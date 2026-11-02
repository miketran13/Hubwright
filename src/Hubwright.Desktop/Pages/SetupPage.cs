using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Auth;
using Hubwright.Collectors;
using Hubwright.Engine;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Stage 1 of the journey, as four steps: prerequisites, register the read-only app (once), sign in
/// with the browser, the Windows account or a device code, then the first scan. Each step shows
/// whether it is done. Also lists every tenant on this PC.
/// </summary>
public sealed class SetupPage : UserControl
{
    private static CancellationTokenSource? _scan;

    private readonly HubwrightConfig _config = HubwrightConfig.Load();
    private readonly TextBlock _account = Ui.Strong("Checking…");
    private readonly TextBlock _pnp = Ui.Muted("Checking…");
    private readonly ContentControl _pnpStep = new();
    private readonly ContentControl _signInStep = new();
    private readonly TextBlock _status = Ui.Muted("");
    private readonly InfoBar _code = new() { Title = "Enter this code", Severity = InfoBarSeverity.Informational, IsClosable = false };
    private readonly TextBox _name;
    private readonly TextBox _client;
    private readonly TextBox _tenantId;
    private readonly RadioButtons _method = new() { Header = "Sign in with", MaxColumns = 3 };
    private readonly TextBox _scope = Ui.Field("Scope (optional)", "", "a hub or site URL, to scan only part of the tenant");
    private readonly NumberBox _maxSites;
    private readonly Button _cancel;

    public SetupPage()
    {
        _name = Ui.Field("Tenant prefix", _config.Tenant.Name ?? "", "found from Graph when you sign in");
        _client = Ui.Field("App (client) id", _config.Tenant.ClientId ?? "", "from Register the app");
        _tenantId = Ui.Field("Directory (tenant) id or domain", _config.Tenant.TenantId ?? "", "contoso.onmicrosoft.com (filled in by Register the app)");
        _maxSites = new NumberBox { Header = "Sites read in detail", Value = _config.Scan.MaxDetailedSites, Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        foreach (var (method, label) in Methods)
        {
            _method.Items.Add(new RadioButton { Content = label, Tag = method });
        }

        _method.SelectedIndex = Array.FindIndex(Methods, m => m.Method == _config.Tenant.SignIn);
        _cancel = Ui.Button("Cancel scan", () => _scan?.Cancel(), glyph: Glyphs.Cancel).With(b => b.Visibility = _scan is null ? Visibility.Collapsed : Visibility.Visible);

        var registered = !string.IsNullOrWhiteSpace(_config.Tenant.ClientId);
        var scanned = _config.Tenant.Name is { } connected && new TenantStore(connected).Snapshots().Count > 0;
        _pnpStep.Content = Step(1, "Prerequisites (needed only to build)", null);
        _signInStep.Content = Step(3, "Sign in", null);

        var prerequisites = Ui.Card(Ui.Stack(10,
            _pnpStep,
            Ui.Muted("PowerShell 7 and PnP PowerShell run the build steps SharePoint's REST API doesn't document (home site, navigation, templates, header, logo). Assessing and planning don't need them, so you can skip this step for now."),
            _pnp,
            Ui.Row(
                Ui.Button("Install PowerShell 7", () => Launch("cmd.exe", ["/k", Tools.PnpShell.InstallPowerShellCommand]), glyph: Glyphs.Download),
                Ui.Button("Install PnP PowerShell", () => Launch(Tools.PnpShell.FindPowerShell() ?? throw new InvalidOperationException("Install PowerShell 7 first."), ["-NoExit", "-NoProfile", "-Command", Tools.PnpShell.InstallPnpCommand]), glyph: Glyphs.Download),
                Ui.Button("Check again", RefreshPnpAsync, glyph: Glyphs.Refresh))));

        var setup = Ui.Card(Ui.Stack(10,
            Step(2, "Register the app (once per tenant)", registered),
            Ui.Muted("Hubwright signs in with your own app in Entra ID. Register the app opens PowerShell, signs you in with your browser, creates \"Hubwright\" with read permissions only, and opens the consent page for an admin. Write permissions are added later, in Build, and only if you build from Hubwright."),
            Ui.Row(
                Ui.Button("Register the app…", RegisterApp, accent: !registered, glyph: Glyphs.Shield),
                Ui.Button("Reload after registering", () => App.Current.Window.Show("setup"), glyph: Glyphs.Refresh)),
            Ui.Muted("Delegated, read-only: Graph Sites.Read.All, Group.Read.All, User.Read.All, Reports.Read.All; SharePoint AllSites.Read."),
            new Expander
            {
                Header = registered ? "Connection details" : "Already have an app? Enter its ids",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = Ui.Stack(10,
                    Ui.Columns(2, [_client, _tenantId, _name, new Border()]),
                    Ui.Button("Save connection", () =>
                    {
                        Save();
                        App.Current.Reload();
                    }, glyph: Glyphs.Save)),
            }));

        var signedIn = Ui.Card(Ui.Stack(10,
            _signInStep,
            Ui.Row(Ui.Icon("people", 22), _account),
            _code.With(c => c.IsOpen = false),
            _method,
            Ui.Row(
                Ui.Button("Sign in", SignInAsync, accent: registered, glyph: Glyphs.Shield),
                Ui.Button("Sign out", SignOutAsync, glyph: Glyphs.Cancel)),
            Ui.Muted("Browser: your default browser opens the Microsoft sign-in page; close the tab when it says you're signed in. Hubwright then finds your SharePoint address from Graph and asks for SharePoint access once.")));

        var scan = Ui.Card(Ui.Stack(10,
            Step(4, "First scan", scanned),
            Ui.Muted("Reads tenant settings, hubs, sites, navigation, permissions and content into a snapshot on this PC. Read-only: GET requests only. Afterwards Home shows your grade and the next step."),
            Ui.Columns(2, [_scope, _maxSites]),
            Ui.Row(Ui.Button("Scan now", ScanAsync, accent: true, glyph: Glyphs.Refresh), _cancel),
            _status));

        Content = new ScrollViewer { HorizontalScrollMode = ScrollMode.Disabled, Content = Ui.Stack(14,
            Ui.Header(Glyphs.Shield, "Setup", "Four steps to connect your tenant. Sign in as a SharePoint Administrator; scans use read-only delegated permissions and snapshots stay on this PC."),
            prerequisites,
            setup,
            signedIn,
            scan,
            Ui.Heading("Tenants on this PC"),
            Tenants()).With(s => s.Padding = new Thickness(32, 24, 32, 40)) };
        Loaded += async (_, _) =>
        {
            await RefreshAccountAsync();
            await RefreshPnpAsync();
        };
    }

    private static readonly (SignInMethod Method, string Label)[] Methods =
    [
        (SignInMethod.Browser, "Browser"),
        (SignInMethod.Windows, "Windows account"),
        (SignInMethod.DeviceCode, "Device code"),
    ];

    private SignInMethod Method => _method.SelectedItem is RadioButton { Tag: SignInMethod m } ? m : SignInMethod.Browser;

    /// <summary>A step title with its state: done, to do, or still being checked (null).</summary>
    private static WrapRow Step(int number, string title, bool? done) => Ui.Row(
        Ui.Badge(done == true ? "✓ Done" : $"Step {number}", done == true ? Ui.Good : Ui.Brand),
        Ui.Strong(title));

    private void Save()
    {
        _config.Tenant.Name = string.IsNullOrWhiteSpace(_name.Text) ? null : TenantStore.Clean(_name.Text);
        _config.Tenant.ClientId = string.IsNullOrWhiteSpace(_client.Text) ? null : _client.Text.Trim();
        _config.Tenant.TenantId = string.IsNullOrWhiteSpace(_tenantId.Text) ? null : _tenantId.Text.Trim();
        _config.Tenant.SignIn = Method;
        _config.Scan.MaxDetailedSites = (int)_maxSites.Value;
        _config.Save();
    }

    private async Task RefreshPnpAsync()
    {
        _pnp.Text = "Checking…";
        var status = await Task.Run(() => Tools.PnpShell.CheckAsync());
        _pnp.Text = (status.Ready ? "✓ " : "✗ ") + status.Detail;
        _pnpStep.Content = Step(1, "Prerequisites (needed only to build)", status.Ready);
    }

    private async Task RefreshAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.Tenant.ClientId))
        {
            _account.Text = "Register the app first (step 2).";
            _signInStep.Content = Step(3, "Sign in", false);
            return;
        }

        try
        {
            var account = await new TenantAuth(_config.Tenant).CurrentAsync();
            _account.Text = account is null
                ? "Not signed in."
                : $"Signed in as {account.Username}{(_config.Tenant.Name is null ? "" : $" · {_config.Tenant.RootUrl}")}";
            _signInStep.Content = Step(3, "Sign in", account is not null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Identity.Client.MsalException)
        {
            _account.Text = "Could not read the sign-in cache: " + ex.Message;
        }
    }

    private async Task SignInAsync()
    {
        var outcome = await SignInCoreAsync();
        App.Current.Switch(outcome.Tenant);
        App.Current.Window.Show("setup");
    }

    private async Task<SignInOutcome> SignInCoreAsync()
    {
        Save();
        if (_config.Tenant.ClientId is null)
        {
            throw new InvalidOperationException("Register the app first (step 2), or enter its client id.");
        }

        _account.Text = Method switch
        {
            SignInMethod.Browser => "Finish signing in in your browser…",
            SignInMethod.DeviceCode => "Waiting for the code to be entered…",
            _ => "Signing in…",
        };
        var queue = DispatcherQueue;
        try
        {
            var outcome = await TenantSignIn.RunAsync(_config, () => App.Current.Window.Handle, prompt =>
            {
                queue.TryEnqueue(() =>
                {
                    _code.Message = $"{prompt.UserCode} at {prompt.Url}";
                    _code.ActionButton = Ui.Button("Copy code and open", () =>
                    {
                        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        data.SetText(prompt.UserCode);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                        Ui.Open(prompt.Url);
                    });
                    _code.IsOpen = true;
                });
                return Task.CompletedTask;
            }, CancellationToken.None);
            _code.IsOpen = false;
            if (outcome.Corrected)
            {
                await Ui.Message(XamlRoot, "Tenant corrected", $"Graph says this tenant is '{outcome.Tenant}', not '{outcome.Typed}'. The connection now uses {outcome.Tenant}.sharepoint.com.");
            }

            return outcome;
        }
        finally
        {
            _code.IsOpen = false;
            await RefreshAccountAsync();
        }
    }

    private async Task SignOutAsync()
    {
        if (_config.Tenant.ClientId is null)
        {
            return;
        }

        await new TenantAuth(_config.Tenant).SignOutAsync();
        new AuditLog().Append("logout", _config.Tenant.Name ?? "");
        await RefreshAccountAsync();
    }

    private async Task ScanAsync()
    {
        Save();
        if (_config.Tenant.ClientId is null || _config.Tenant.Name is null || await new TenantAuth(_config.Tenant).CurrentAsync() is null)
        {
            await SignInCoreAsync();
        }

        // A session of its own: switching the app's session now would rebuild this page mid-scan.
        var session = HubwrightSession.Open(HubwrightConfig.Load().Tenant.Name!, App.CliPath);
        var auth = session.Auth(() => App.Current.Window.Handle);
        var scope = string.IsNullOrWhiteSpace(_scope.Text) ? null : _scope.Text.Trim();
        var progress = new Progress<ScanProgress>(p => Activity.Report(_status.Text = p.Total > 0 ? $"{p.Step} {p.Done}/{p.Total}" : p.Step + "…"));
        _scan = new CancellationTokenSource();
        _cancel.Visibility = Visibility.Visible;
        try
        {
            var snapshot = await Task.Run(() => session.Collector(auth).CollectAsync(scope, progress, _scan.Token));
            var path = session.Store.SaveSnapshot(snapshot);
            session.Audit.Append("scan", $"{snapshot.Sites.Count} sites, {snapshot.ScanIssues.Count} issues → {path}");
            App.Current.Switch(session.Store.Tenant);
            App.Current.Window.Show("home");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Scan cancelled. Nothing was saved.";
        }
        finally
        {
            _scan.Dispose();
            _scan = null;
            _cancel.Visibility = Visibility.Collapsed;
        }
    }

    private void RegisterApp()
    {
        var script = App.ToolPath("register-app.ps1") ?? throw new InvalidOperationException("tools/register-app.ps1 was not found next to Hubwright.");
        var shell = App.FindOnPath("pwsh.exe") ?? "powershell.exe";
        var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Pause" })
        {
            start.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(_name.Text))
        {
            start.ArgumentList.Add("-Tenant");
            start.ArgumentList.Add(TenantStore.Clean(_name.Text));
        }

        if (App.CliPath is { } cli)
        {
            start.ArgumentList.Add("-Hubwright");
            start.ArgumentList.Add(cli);
        }

        var process = System.Diagnostics.Process.Start(start);
        if (process is null)
        {
            return;
        }

        var queue = DispatcherQueue;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            process.Dispose();
            queue.TryEnqueue(() =>
            {
                App.Current.Reload();
                App.Current.Window.Show("setup");
            });
        };
    }

    private static void Launch(string file, string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    private static StackPanel Tenants()
    {
        var list = Ui.Stack(8);
        var current = App.Current.Session.Store.Tenant;
        foreach (var name in TenantStore.Tenants())
        {
            var store = new TenantStore(name);
            var latest = store.Snapshots().FirstOrDefault();
            list.Children.Add(Ui.Card(Ui.Row(
                Ui.Strong(name).With(t => t.MinWidth = 180),
                name == current ? Ui.Badge("open", Ui.Brand) : new TextBlock(),
                Ui.Muted($"{store.Snapshots().Count} snapshot(s) · {store.Blueprints().Count} blueprint(s){(latest is null ? "" : " · latest " + Path.GetFileNameWithoutExtension(latest))}"),
                Ui.Button("Open", () =>
                {
                    App.Current.Switch(name);
                    App.Current.Window.Show("home");
                }),
                Ui.Button("Folder", () => Ui.Open(store.Directory), glyph: Glyphs.Open))));
        }

        if (list.Children.Count == 0)
        {
            list.Children.Add(Ui.Muted("None yet."));
        }

        return list;
    }
}
