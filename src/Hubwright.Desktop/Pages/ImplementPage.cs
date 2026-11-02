using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Auth;
using Hubwright.Engine;
using Hubwright.Implementation;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Implements a proposal from the desktop. Automatic steps call documented, allow-listed APIs with
/// the Hubwright app's write permissions; guided steps open in the browser pane on the right with what to
/// enter. Select everything or single steps; preview the requests; run; resume later.
/// </summary>
public sealed partial class ImplementPage : Grid
{
    private static CancellationTokenSource? _run;

    private readonly HubwrightSession _session = App.Current.Session;
    private readonly ComboBox _proposals = new() { Header = "Proposal", MinWidth = 300 };
    private readonly List<string> _folders;
    private readonly StackPanel _list = Ui.Stack(4);
    private readonly TextBlock _status = Ui.Muted("");
    private readonly TextBlock _account = Ui.Muted("");
    private readonly TextBlock _pnpStatus = Ui.Muted("Checking PowerShell 7 and PnP PowerShell…");
    private readonly Dictionary<string, (CheckBox Box, Border Badge, TextBlock Result)> _rows = [];
    private readonly ContentControl _detail = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly WebView2 _browser = new() { MinHeight = 420 };
    private readonly WebView2 _structure = new();
    private readonly WebView2 _look = new();
    private readonly WebView2 _sites = new();
    private string? _sitesHtml;
    private readonly Pivot _tabs = new();
    private readonly Hints.HintBook _hints = App.Current.Session.Skills.HintBook();
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly bool _sample;

    // Security mode: the baseline for a profile instead of a proposal's build plan.
    private readonly bool _security;
    private readonly ComboBox _profile = new() { Header = "Profile", MinWidth = 200, ItemsSource = Enum.GetNames<Security.BaselineProfile>(), SelectedIndex = 0 };
    private ImplementationPlan? _plan;
    private string? _folder;

    public ImplementPage(string? folder = null, bool security = false)
    {
        _security = security;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(560) });
        ColumnDefinitions.Add(new ColumnDefinition());
        _folders = [.. _session.Store.IntranetProposals()];
        _sample = _session.Workspace.Snapshot?.Source is SnapshotSource.Sample or SnapshotSource.Blueprint;
        foreach (var f in _folders)
        {
            _proposals.Items.Add(Path.GetFileName(f));
        }

        _runButton = Ui.Button("Run selected", RunAsync, accent: true, glyph: Glyphs.Rocket);
        _stopButton = Ui.Button("Stop", () => _run?.Cancel(), glyph: Glyphs.Cancel).With(b => b.Visibility = _run is null ? Visibility.Collapsed : Visibility.Visible);
        var left = Ui.Stack(12,
            _security
                ? Ui.Header(Glyphs.Shield, "Security baseline", "Standard, Strict or Regulated: each setting is read first, changed only after you confirm, recorded so it can be restored, and cited on the right.")
                : Ui.Header(Glyphs.Rocket, "Build", "Turn the plan into the intranet: automatic steps run here after you confirm; guided steps open on the right."),
            _sample ? Ui.InfoBar("Sample or design-only data", "This tenant is not a real scan, so you can preview the plan and its requests but not run it.", InfoBarSeverity.Warning) : new Border(),
            Connection(),
            Setup(),
            _security
                ? Ui.Row(_profile, Ui.Button("Restore previous values…", RestoreAsync, glyph: Glyphs.History)).With(r => r.VerticalAlignment = VerticalAlignment.Bottom)
                : _proposals,
            Ui.Row(
                Ui.Button("Automatic", () => Select(s => s.Kind == StepKind.Automatic && s.Risk != StepRisk.Caution), glyph: Glyphs.Check),
                Ui.Button("All", () => Select(_ => true)),
                Ui.Button("None", () => Select(_ => false)),
                Ui.Button("Preview requests", PreviewAsync, glyph: Glyphs.Page)),
            Ui.Row(_runButton, _stopButton, Ui.Button("Check the tenant", () => CheckAsync(), glyph: Glyphs.Check), Ui.Button("Start over", RebuildAsync, glyph: Glyphs.Refresh)),
            _status,
            _list);
        Children.Add(new ScrollViewer { Content = left, Padding = new Thickness(24, 16, 16, 24) });
        var look = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, RowSpacing = 8 };
        look.Children.Add(Ui.Row(
            Ui.Button("Edit theme and logo…", () => App.Current.Window.Show("theme-editor", _folder), accent: true, glyph: Glyphs.Palette),
            Ui.Muted("The home page in the proposal's theme, with its logo. Edits update the theme steps.")));
        SetRow(_look, 1);
        look.Children.Add(_look);
        _tabs.Items.Add(new PivotItem { Header = "Step", Content = new ScrollViewer { Content = _detail, Padding = new Thickness(4, 8, 12, 12) } });
        if (!_security)
        {
        _tabs.Items.Add(new PivotItem { Header = "Structure", Content = _structure });
        var sitesTab = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, RowSpacing = 8 };
        sitesTab.Children.Add(Ui.Row(
            Ui.Button("Back", () => { if (_sites.CanGoBack) { _sites.GoBack(); } }, glyph: "\uE72B"),
            Ui.Button("Forward", () => { if (_sites.CanGoForward) { _sites.GoForward(); } }, glyph: "\uE72A"),
            Ui.Button("Home site", () => _ = _sites.ExecuteScriptAsync("go(D.home)"), glyph: Glyphs.Home),
            Ui.Button("Open in browser", OpenSitesInBrowser, glyph: Glyphs.Open),
            Ui.Muted("Every proposed site with sample content, in the new navigation and theme.")));
        SetRow(_sites, 1);
        sitesTab.Children.Add(_sites);
        _tabs.Items.Add(new PivotItem { Header = "Site preview", Content = sitesTab });
        _tabs.Items.Add(new PivotItem { Header = "Look", Content = look });
        }

        _tabs.Margin = new Thickness(0, 8, 8, 8);
        SetColumn(_tabs, 1);
        Children.Add(_tabs);
        _detail.Content = Ui.Stack(8, Ui.Strong("Select a step"), Ui.Muted("Automatic steps show the exact request Hubwright sends. Guided steps open the page to use here, with the values to enter."));

        _proposals.SelectionChanged += (_, _) => Load();
        _profile.SelectionChanged += (_, _) => Load();
        if (_security)
        {
            Load();
        }
        else if (_folders.Count == 0)
        {
            _list.Children.Add(Ui.InfoBar("No proposal yet", "Create one on the Intranet proposal page first.", InfoBarSeverity.Informational));
        }
        else
        {
            _proposals.SelectedIndex = folder is null ? 0 : Math.Max(0, _folders.IndexOf(folder));
        }

        Loaded += async (_, _) =>
        {
            await RefreshAccountAsync();
            await RefreshPnpAsync();
        };
    }

    private Border Connection() => Ui.Card(Ui.Stack(8,
        Ui.Strong("Write permissions"),
        Ui.Muted("Automatic steps use the same Hubwright app with its write permissions (create sites, register hubs, themes, sharing settings). Sign in as a SharePoint Administrator and consent once; every write is in the Audit log. Guided steps need none of this."),
        Ui.Row(
            Ui.Button("Grant write permissions", async () =>
            {
                _account.Text = "Finish in your browser if it asks…";
                var account = await TenantSignIn.ImplementAsync(HubwrightConfig.Load(), () => App.Current.Window.Handle, null, CancellationToken.None);
                _account.Text = $"{account.Username} can implement.";
            }, accent: true, glyph: Glyphs.People),
            Ui.Button("Add write permissions to the app…", RegisterApp, glyph: Glyphs.Shield)),
        _account));

    /// <summary>PowerShell 7 and PnP PowerShell, which run the home site, navigation, template, header and logo steps.</summary>
    private Border Setup() => Ui.Card(Ui.Stack(8,
        Ui.Strong("Setup: PnP PowerShell"),
        Ui.Muted("The home site, navigation, Microsoft templates, look book packages, site headers and logos run as PnP PowerShell scripts that Hubwright writes and runs for you, with your sign-in. PnP PowerShell is open source (no Microsoft SLA) and needs PowerShell 7.4 or later."),
        _pnpStatus,
        Ui.Row(
            Ui.Button("Install PowerShell 7", () => Launch("cmd.exe", ["/k", Tools.PnpShell.InstallPowerShellCommand]), glyph: Glyphs.Download),
            Ui.Button("Install PnP PowerShell", () => Launch(Tools.PnpShell.FindPowerShell() ?? throw new InvalidOperationException("Install PowerShell 7 first."), ["-NoExit", "-NoProfile", "-Command", Tools.PnpShell.InstallPnpCommand]), accent: true, glyph: Glyphs.Download),
            Ui.Button("Check again", RefreshPnpAsync, glyph: Glyphs.Refresh))));

    private async Task RefreshPnpAsync()
    {
        _pnpStatus.Text = "Checking…";
        var status = await Task.Run(() => Tools.PnpShell.CheckAsync());
        _pnpStatus.Text = (status.Ready ? "✓ " : "✗ ") + status.Detail;
    }

    /// <summary>Opens a visible window the admin watches (installers ask their own questions there).</summary>
    private void Launch(string file, string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
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
            queue.TryEnqueue(() => _ = RefreshPnpAsync());
        };
    }

    private async Task RefreshAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(_session.Config.Tenant.ClientId))
        {
            _account.Text = "No app yet: connect on the Connection page first.";
            return;
        }

        try
        {
            var auth = new TenantAuth(_session.Config.Tenant, null, AccessProfile.Implement);
            var account = await auth.CurrentAsync();
            if (account is null)
            {
                _account.Text = "Not signed in: sign in on the Connection page, then grant write permissions.";
                return;
            }

            await auth.TokenAsync(TenantResource.Graph);
            _account.Text = $"{account.Username} can implement.";
        }
        catch (SignInRequiredException)
        {
            _account.Text = "Write permissions not granted yet for this sign-in: select Grant write permissions.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Microsoft.Identity.Client.MsalException)
        {
            _account.Text = ex.Message;
        }
    }
    private Security.BaselineProfile Profile => Enum.Parse<Security.BaselineProfile>((string)_profile.SelectedItem);

    private void Load()
    {
        if (!_security && _proposals.SelectedIndex < 0)
        {
            return;
        }

        try
        {
            if (_security)
            {
                _folder = SecurityBaselines.Folder(_session, Profile);
                _plan = SecurityBaselines.For(_session, Profile);
            }
            else
            {
                _folder = _folders[_proposals.SelectedIndex];
                _plan = Implementations.For(_session, _folder);
            }
        }
        catch (InvalidOperationException ex)
        {
            _list.Children.Clear();
            _list.Children.Add(Ui.InfoBar("Can't build the plan", ex.Message, InfoBarSeverity.Error));
            return;
        }

        Fill();
        _ = PreviewsAsync();
    }

    /// <summary>The structure (with each site's implementation status) and the look, for the selected proposal.</summary>
    private async Task PreviewsAsync()
    {
        if (_security || _folder is null || _plan is null)
        {
            return;
        }

        var proposal = TenantStore.LoadIntranetProposal(_folder);
        var statuses = Implementations.SiteStatuses(_plan);
        if (_structure.CoreWebView2 is null)
        {
            await _structure.EnsureCoreWebView2Async();
            _structure.CoreWebView2!.WebMessageReceived += (_, e) => OpenSite(e.WebMessageAsJson);
        }

        _structure.NavigateToString(Proposals.StructurePreview.Render(proposal, _session.Workspace.Snapshot, _hints, statuses));
        await _look.EnsureCoreWebView2Async();
        var logo = proposal.Blueprint.Logo is { } path && File.Exists(Path.Combine(_folder, path))
            ? $"data:{(path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : "image/png")};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(_folder, path)))}"
            : null;
        _look.NavigateToString(Preview.MockupRenderer.Render(Preview.MockupModel.FromBlueprint(proposal.Blueprint).WithLogo(logo)));
        await _sites.EnsureCoreWebView2Async();
        _sitesHtml = Proposals.SitePrototype.Render(proposal, _session.Skills.Templates, logo, statuses);
        _sites.NavigateToString(_sitesHtml);
    }

    /// <summary>"Open in site preview" from the Structure tab: switch tabs and show that site.</summary>
    private void OpenSite(string json)
    {
        try
        {
            var url = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("url").GetString();
            if (url is null)
            {
                return;
            }

            _tabs.SelectedIndex = 2;
            _ = _sites.ExecuteScriptAsync($"hubwrightOpen({System.Text.Json.JsonSerializer.Serialize(url)})");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            App.Log("structure message: " + ex.Message);
        }
    }

    private void OpenSitesInBrowser()
    {
        if (_sitesHtml is null || _folder is null)
        {
            return;
        }

        var path = Path.Combine(_folder, "site-preview.html");
        File.WriteAllText(path, _sitesHtml);
        Ui.Open(path);
    }

    private void Fill()
    {
        _list.Children.Clear();
        _rows.Clear();
        if (_plan is null)
        {
            return;
        }

        var automatic = _plan.Steps.Count(s => s.Kind == StepKind.Automatic);
        _status.Text = $"{_plan.Steps.Count(s => s.IsFinished)} of {_plan.Steps.Count} steps finished · {automatic} automatic, {_plan.Steps.Count - automatic} guided.";
        foreach (var phase in _plan.Steps.GroupBy(s => s.Phase))
        {
            _list.Children.Add(Ui.Heading(phase.Key));
            foreach (var step in phase)
            {
                var box = new CheckBox { IsChecked = false, MinWidth = 0, IsEnabled = !step.IsFinished };
                var badge = StatusBadge(step);
                var result = Ui.Muted(step.Result ?? "");
                var title = new HyperlinkButton { Content = new TextBlock { Text = step.Title, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
                title.Click += (_, _) => Show(step);
                var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                row.Children.Add(box);
                var badges = Ui.Row(KindBadge(step), RiskBadge(step));
                if (step.Target is { } site && Uri.TryCreate(site, UriKind.Absolute, out var siteUri))
                {
                    badges.Children.Add(new HyperlinkButton { Content = "Open ↗", NavigateUri = siteUri, Padding = new Thickness(4, 0, 4, 0), FontSize = 12 });
                }

                var text = Ui.Stack(2, title, badges, result);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                Grid.SetColumn(badge, 2);
                row.Children.Add(badge);
                _rows[step.Id] = (box, badge, result);
                _list.Children.Add(Ui.Card(row).With(c => c.Padding = new Thickness(10, 6, 10, 6)));
            }
        }
    }

    private static Border KindBadge(ImplementationStep s) => Ui.Badge(s.Kind == StepKind.Automatic ? "automatic" : "guided", s.Kind == StepKind.Automatic ? Ui.Brand : Ui.Info);

    private static Border RiskBadge(ImplementationStep s) => Ui.Badge(s.Risk switch { StepRisk.Adds => "adds", StepRisk.Changes => "changes", _ => "caution" }, s.Risk switch { StepRisk.Adds => Ui.Good, StepRisk.Changes => Ui.Watch, _ => Ui.Problem });

    private static Border StatusBadge(ImplementationStep s) => Ui.Badge(s.Status switch
    {
        StepStatus.AlreadyDone => "in place",
        StepStatus.Running => "running…",
        _ => s.Status.ToString().ToLowerInvariant(),
    }, s.Status switch
    {
        StepStatus.Done or StepStatus.AlreadyDone => Ui.Good,
        StepStatus.Failed => Ui.Problem,
        StepStatus.Blocked or StepStatus.Running => Ui.Watch,
        _ => Ui.Neutral,
    });

    private void Select(Func<ImplementationStep, bool> which)
    {
        if (_plan is null)
        {
            return;
        }

        foreach (var step in _plan.Steps)
        {
            if (_rows.TryGetValue(step.Id, out var row) && row.Box.IsEnabled)
            {
                row.Box.IsChecked = which(step);
            }
        }
    }

    private List<string> Checked() => [.. _rows.Where(r => r.Value.Box.IsChecked == true).Select(r => r.Key)];

    /// <summary>Adds the write permissions to the existing Hubwright app (tools/register-app.ps1 -AppId), in a PowerShell window the admin sees.</summary>
    private void RegisterApp()
    {
        var script = App.ToolPath("register-app.ps1") ?? throw new InvalidOperationException("tools/register-app.ps1 was not found next to Hubwright.");
        var start = new System.Diagnostics.ProcessStartInfo(App.FindOnPath("pwsh.exe") ?? "powershell.exe") { UseShellExecute = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-AppId", _session.Config.Tenant.ClientId ?? throw new InvalidOperationException("Connect on the Setup page first."), "-Write", "-Pause" })
        {
            start.ArgumentList.Add(argument);
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
        var folder = _folder;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            process.Dispose();
            queue.TryEnqueue(() =>
            {
                App.Current.Reload();
                App.Current.Window.Show("build", folder);
            });
        };
    }

    private static void Copy(string text)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }
}
