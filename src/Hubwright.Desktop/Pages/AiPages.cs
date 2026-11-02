using Microsoft.Extensions.AI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Agents;
using Hubwright.Blueprints;
using Hubwright.Config;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Skills;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>Chat with a role, grounded on the snapshot through Hubwright's read-only tools.</summary>
public sealed class AskPage : Grid
{
    private readonly StackPanel _messages = Ui.Stack(10);
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input = new() { PlaceholderText = "Ask about your intranet: which sites are overshared? what should we fix first?", AcceptsReturn = false };
    private readonly ComboBox _role = Ui.Choice("Role", Roles.All.Where(r => r != Roles.Librarian), Roles.Assistant);
    private readonly List<ChatMessage> _history = [];

    public AskPage()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Padding = new Thickness(32, 24, 32, 20);
        RowSpacing = 12;
        var session = App.Current.Session;
        var backend = session.Config.Ai.Backend;
        Children.Add(Ui.Stack(8,
            Ui.Header(Glyphs.Chat, "Ask", $"On {ModelCatalog.BackendLabel(backend)} · {session.Models.CheckHealth(backend).Detail}"),
            Ui.Row(_role,
                Suggestion("Write our 30/60/90-day plan", Roles.Assessor, "Write the assessment summary and the 30/60/90-day plan for this tenant."),
                Suggestion("Propose an accessible theme", Roles.Brand, "Propose an accessible theme for our brand colours and the header layout you'd use."),
                Suggestion("Draft our governance", Roles.Governance, "Draft our intranet governance page.")).BottomAligned()));
        _scroll = new ScrollViewer { Content = _messages };
        SetRow(_scroll, 1);
        Children.Add(_scroll);
        var send = Ui.Button("Send", () => SendAsync(_input.Text, null), accent: true);
        var bar = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        bar.Children.Add(_input);
        Grid.SetColumn(send, 1);
        bar.Children.Add(send);
        SetRow(bar, 2);
        Children.Add(bar);
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await SendAsync(_input.Text, null);
            }
        };
    }

    private Button Suggestion(string text, string role, string prompt) => Ui.Button(text, () => SendAsync(prompt, role), glyph: Glyphs.Sparkle);

    private async Task SendAsync(string text, string? role)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        role ??= (string)_role.SelectedItem;
        _input.Text = "";
        _messages.Children.Add(Ui.Card(Ui.Text(text)).With(c => c.HorizontalAlignment = HorizontalAlignment.Right));
        var thinking = Ui.Muted($"{Roles.Title(role)} is working…");
        _messages.Children.Add(thinking);

        // From the Enter key there is no button to show it; then the strip at the top does.
        using var running = Activity.IsBusy ? null : Activity.Begin($"{Roles.Title(role)} is working");
        try
        {
            var runner = App.Current.Session.Runner;
            var history = _history.ToList();
            var answer = await Task.Run(() => runner.RunAsync(role, text, history: history));
            _history.Add(new ChatMessage(ChatRole.User, text));
            _history.Add(new ChatMessage(ChatRole.Assistant, answer));
            _messages.Children.Remove(thinking);
            _messages.Children.Add(Ui.Card(Ui.Stack(4, Ui.Muted(Roles.Title(role)), Ui.Text(answer))));
        }
        catch (Exception ex)
        {
            _messages.Children.Remove(thinking);
            _messages.Children.Add(Ui.InfoBar("The AI could not answer", ex.Message + " Check Settings → AI backend.", InfoBarSeverity.Error));
        }

        _scroll.ChangeView(null, _scroll.ScrollableHeight + 1000, null);
    }
}

/// <summary>What agents and refreshes suggest; nothing takes effect until accepted here.</summary>
public static class ProposalsPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var list = Ui.Stack(10);
        foreach (var p in session.Proposals.All())
        {
            var body = Ui.Stack(8, Ui.Text(p.Summary, 13));
            foreach (var (name, content) in p.Files)
            {
                body.Children.Add(new Expander { Header = name, Content = Ui.Area("", content, 260), HorizontalAlignment = HorizontalAlignment.Stretch });
            }

            if (p.Kind == ProposalKind.SiteDesign)
            {
                body.Children.Add(Ui.Button("Open in Site designs (preview, accept or reject)", () => App.Current.Window.Show("designs", Agents.SiteDesignProposals.Read(p).Url), glyph: Glyphs.Open));
            }

            if (p.Status == ProposalStatus.Pending)
            {
                body.Children.Add(Ui.Row(
                    Ui.Button("Accept", () => Decide(p, accept: true), accent: true, glyph: Glyphs.Check),
                    Ui.Button("Reject", () => Decide(p, accept: false), glyph: Glyphs.Cancel)));
            }

            list.Children.Add(new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Header = Ui.Row(Ui.Badge(p.Status.ToString(), p.Status == ProposalStatus.Pending ? Ui.Watch : p.Status == ProposalStatus.Accepted ? Ui.Good : Ui.Neutral), Ui.Strong(p.Title), Ui.Muted($"{p.Kind} · by {p.ProposedBy} · {p.Created:yyyy-MM-dd HH:mm}")),
                Content = body,
            });
        }

        if (list.Children.Count == 0)
        {
            list.Children.Add(Ui.InfoBar("No proposals", "When an agent proposes a blueprint or a skill refresh finds a changed source, it shows up here.", InfoBarSeverity.Informational));
        }

        return Ui.Page(Ui.Header(Glyphs.Inbox, "Proposals", "Agents and skill refreshes only propose. You decide."), list);
    }

    internal static void Decide(Proposal p, bool accept, bool reload = true)
    {
        var session = App.Current.Session;
        if (accept)
        {
            if (p.Kind == ProposalKind.Blueprint)
            {
                new TenantStore(p.Target).SaveBlueprint(Blueprint.FromJson(p.Files["blueprint.json"]));
            }
            else if (p.Kind == ProposalKind.SiteDesign)
            {
                SiteDesigns.Accept(session, p);
            }
            else if (session.Skills.Find(p.Target) is { } skill)
            {
                var dir = SkillRefresher.Fork(skill, DateOnly.FromDateTime(DateTime.UtcNow));
                if (p.Files.TryGetValue("librarian.md", out var notes))
                {
                    File.WriteAllText(Path.Combine(dir, "REVIEW.md"), notes);
                }

                Ui.Open(dir);
            }
        }

        p.Status = accept ? ProposalStatus.Accepted : ProposalStatus.Rejected;
        p.DecidedBy = Environment.UserName;
        p.Decided = DateTimeOffset.UtcNow;
        session.Proposals.Save(p);
        session.Audit.Append($"proposal.{(accept ? "accept" : "reject")}", $"{p.Id} {p.Title}");
        if (reload)
        {
            App.Current.Reload();
        }
    }
}

/// <summary>The skills in effect: their versions, sources and checks; validate, refresh, fork or add one.</summary>
public static class SkillsPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var issues = SkillValidator.Validate(session.Skills, DateOnly.FromDateTime(DateTime.UtcNow));
        var status = Ui.Muted("");
        var list = Ui.Stack(8);
        foreach (var skill in session.Skills.Skills)
        {
            var body = Ui.Stack(6, Ui.Text(skill.Guidance, 13));
            foreach (var source in skill.Sources)
            {
                body.Children.Add(Ui.Row(Ui.Link(source.Title ?? source.Url, source.Url), Ui.Muted($"reviewed {source.Reviewed:yyyy-MM-dd}{(source.Note is null ? "" : " · " + source.Note)}")));
            }

            body.Children.Add(Ui.Text(string.Join("\n", skill.Rules.Select(r => $"{(r.Basis == Rules.Basis.Documented ? "📘" : "🧭")} {r.Id}: {r.Title}")), 12));
            body.Children.Add(Ui.Row(
                Ui.Button("Open folder", () => Ui.Open(skill.Directory), glyph: Glyphs.Open),
                Ui.Button("Make my own copy", () =>
                {
                    Ui.Open(SkillRefresher.Fork(skill, DateOnly.FromDateTime(DateTime.UtcNow)));
                    App.Current.Reload();
                })));
            var own = issues.Where(i => i.SkillId == skill.Id).ToList();
            list.Children.Add(new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Header = Ui.Row(Ui.Strong(skill.Title), Ui.Badge(skill.Version, Ui.Brand), Ui.Muted($"{skill.Layer} · {skill.Rules.Count} rules"), own.Count > 0 ? Ui.Badge($"{own.Count} issue(s)", Ui.Watch) : new TextBlock()),
                Content = body,
            });
        }

        var newId = Ui.Field("New skill id", "", "my-governance-rules");
        var useAi = new CheckBox { Content = $"Ask the librarian (AI: {session.Config.Ai.BackendFor(Agents.Roles.Librarian)}) to draft the edits" };
        var drift = Ui.Stack(4);
        return Ui.Page(
            Ui.Header(Glyphs.Library, "Skills", "Best practice as data: each skill is SKILL.md plus rules.yaml. Built-in skills ship with Hubwright; your copies in ~/.hubwright/skills replace them by id."),
            Ui.Row(
                Ui.Button("Check sources for changes", async () =>
                {
                    status.Text = "Checking every documented rule's quote against its source…";
                    drift.Children.Clear();
                    var ai = useAi.IsChecked == true;
                    var queue = status.DispatcherQueue;
                    var (results, proposals) = await Task.Run(() => new SkillRefresher(session, new HttpSourceFetcher()).RefreshAsync(session.Skills.Skills, ai, m => queue.TryEnqueue(() => status.Text = m), CancellationToken.None));
                    status.Text = $"{results.Count(r => r.Status == DriftStatus.QuoteFound)} of {results.Count} quotes still match · {proposals.Count} proposal(s) filed{(proposals.Count > 0 ? ": see Proposals" : "")}.";
                    foreach (var r in results.Where(r => r.Status != DriftStatus.QuoteFound))
                    {
                        drift.Children.Add(Ui.Row(Ui.Badge(r.Status.ToString(), r.Status == DriftStatus.QuoteMissing ? Ui.Problem : Ui.Watch), Ui.Strong(r.RuleId), Ui.Link(r.Url, r.Url), Ui.Muted(r.Detail ?? "")));
                    }
                }, accent: true, glyph: Glyphs.Refresh),
                Ui.Button("Validate", () => App.Current.Reload(), glyph: Glyphs.Check),
                Ui.Button("Open my skills folder", () => Ui.Open(HubwrightSession.UserSkillsDirectory), glyph: Glyphs.Open)),
            useAi,
            status,
            drift,
            issues.Count == 0 ? Ui.InfoBar("All skills valid", $"{session.Skills.Rules.Count} rules, every documented rule cited.", InfoBarSeverity.Success) : Ui.InfoBar($"{issues.Count} issue(s)", string.Join("\n", issues.Take(8)), issues.Any(i => i.Level == IssueLevel.Error) ? InfoBarSeverity.Error : InfoBarSeverity.Warning),
            Ui.Row(newId, Ui.Button("Create skill", () =>
            {
                Ui.Open(SkillScaffold.Create(HubwrightSession.UserSkillsDirectory, newId.Text.Trim(), newId.Text.Trim(), DateOnly.FromDateTime(DateTime.UtcNow)));
                App.Current.Reload();
            }).With(b => b.VerticalAlignment = VerticalAlignment.Bottom)),
            list);
    }
}

/// <summary>AI backend and model, the tenant connection, and the organization's skill folder.</summary>
public static class SettingsPage
{
    private static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static UIElement Render()
    {
        var config = HubwrightConfig.Load();
        var backend = Ui.Choice("Default AI backend", Enum.GetNames<ModelBackend>(), config.Ai.Backend.ToString());
        var health = Ui.Muted("");
        void Health() => health.Text = new Hubwright.Chat.ChatClientFactory(config.Ai).CheckHealth(Enum.Parse<ModelBackend>((string)backend.SelectedItem)).Detail;
        backend.SelectionChanged += (_, _) => Health();
        Health();
        var foundry = Ui.Field("Azure AI Foundry endpoint", config.Ai.Foundry.Endpoint ?? "", "https://<resource>.openai.azure.com/");
        var deployment = Ui.Field("Foundry deployment", config.Ai.Foundry.Deployment);
        var local = Ui.Field("Local endpoint (OpenAI-compatible)", config.Ai.Local.Endpoint);
        var localModel = Ui.Field("Local model", config.Ai.Local.Model);
        var claude = Ui.Field("Claude CLI model", config.Ai.ClaudeModel ?? "", "sonnet, opus, haiku");
        var orgSkills = Ui.Field("Organization skills folder", config.Skills.OrganizationDirectory ?? "", @"\\server\share\hubwright-skills");
        var maxSites = new NumberBox { Header = "Sites read in detail per scan", Value = config.Scan.MaxDetailedSites, Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

        var theme = Ui.Choice("Theme", ["System", "Light", "Dark"], config.Desktop.Theme is "Light" or "Dark" ? config.Desktop.Theme : "System");

        // Menus, drop-downs and tooltips keep the theme the app started with: a restart finishes a switch.
        var restart = Ui.Button("Restart to finish switching", () => App.Current.Restart("settings"), glyph: Glyphs.Refresh)
            .With(b => b.Visibility = config.Desktop.Theme != App.Current.StartupTheme ? Visibility.Visible : Visibility.Collapsed);
        theme.SelectionChanged += (_, _) =>
        {
            // Saved and applied at once: it changes nothing but the look.
            var chosen = HubwrightConfig.Load();
            chosen.Desktop.Theme = (string)theme.SelectedItem;
            chosen.Save();
            App.Current.Session.Config.Desktop.Theme = chosen.Desktop.Theme;
            restart.Visibility = chosen.Desktop.Theme != App.Current.StartupTheme ? Visibility.Visible : Visibility.Collapsed;
            App.Current.Window.ApplyTheme(chosen.Desktop.Theme);
        };

        return Ui.Page(
            Ui.Header(Glyphs.Settings, "Settings", HubwrightConfig.DefaultPath),
            Ui.Heading("Appearance"),
            Ui.Card(Ui.Stack(10, Ui.Row(theme, restart).BottomAligned(),
                Ui.Muted("System follows Windows (Settings → Personalization → Colors). The switch at the bottom of the navigation flips between light and dark too. Pages switch at once; menus and drop-downs follow after a restart. Previews of SharePoint pages keep SharePoint's own colours."))),
            Ui.Heading("AI"),
            Ui.Card(Ui.Stack(10, backend, health, Ui.Columns(2, [foundry, deployment, local, localModel, claude]),
                Ui.Muted("API keys are not stored here: set ANTHROPIC_API_KEY, or sign in to Azure (az login) for Foundry. Local backends (Ollama, Foundry Local, LM Studio) keep tenant data on this machine."))),
            Ui.Heading("Skills and scans"),
            Ui.Card(Ui.Stack(10, orgSkills, maxSites)),
            Ui.Button("Save", () =>
            {
                config.Ai.Backend = Enum.Parse<ModelBackend>((string)backend.SelectedItem);
                config.Ai.Foundry.Endpoint = string.IsNullOrWhiteSpace(foundry.Text) ? null : foundry.Text.Trim();
                config.Ai.Foundry.Deployment = deployment.Text.Trim();
                config.Ai.Local.Endpoint = local.Text.Trim();
                config.Ai.Local.Model = localModel.Text.Trim();
                config.Ai.ClaudeModel = string.IsNullOrWhiteSpace(claude.Text) ? null : claude.Text.Trim();
                config.Skills.OrganizationDirectory = string.IsNullOrWhiteSpace(orgSkills.Text) ? null : orgSkills.Text.Trim();
                config.Scan.MaxDetailedSites = (int)maxSites.Value;
                config.Desktop.Theme = (string)theme.SelectedItem;
                config.Save();
                App.Current.Reload();
            }, accent: true, glyph: Glyphs.Save),
            Ui.Heading("Health"),
            HealthCard(),
            Ui.Heading("Advanced"),
            Ui.Card(Ui.Stack(8,
                Ui.Muted("The facts rule expressions can use, for writing your own skills."),
                Ui.Button("Open facts", () => App.Current.Window.Show("facts"), glyph: Glyphs.Tree))),
            Ui.Heading("Use Hubwright from an AI client (MCP)"),
            McpCard());
    }

    /// <summary>What <c>hubwright doctor</c> prints: home, tenant, snapshot, skills and every AI backend.</summary>
    private static Border HealthCard()
    {
        var session = App.Current.Session;
        var lines = Ui.Stack(6,
            Ui.Row(Ui.Muted("Home").With(t => t.MinWidth = 90), Ui.Text(Runtime.HubwrightHome.Directory, 13), Ui.Button("Open", () => Ui.Open(Runtime.HubwrightHome.Directory), glyph: Glyphs.Open)),
            Ui.Row(Ui.Muted("Tenant").With(t => t.MinWidth = 90), Ui.Text($"{session.Store.Tenant}{(session.Config.Tenant.ClientId is null ? " (not connected)" : $" · app {session.Config.Tenant.ClientId} · {session.Config.Tenant.SignIn}")}", 13)),
            Ui.Row(Ui.Muted("Snapshot").With(t => t.MinWidth = 90), Ui.Text(session.Store.Snapshots().FirstOrDefault() ?? "none", 13)),
            Ui.Row(Ui.Muted("Skills").With(t => t.MinWidth = 90), Ui.Text($"{session.Skills.Skills.Count} loaded, {session.Skills.Rules.Count} rules, {session.Skills.Errors.Count} errors", 13)),
            Ui.Row(Ui.Muted("CLI").With(t => t.MinWidth = 90), Ui.Text(App.CliPath ?? "not found: Claude and Copilot CLI backends get no tools", 13)));
        foreach (var error in session.Skills.Errors)
        {
            lines.Children.Add(Ui.InfoBar(error.Directory, error.Message, InfoBarSeverity.Error));
        }

        foreach (var backend in Enum.GetValues<ModelBackend>())
        {
            var health = session.Models.CheckHealth(backend);
            lines.Children.Add(Ui.Row(Ui.Badge(health.Ready ? "ready" : "not set up", health.Ready ? Ui.Good : Ui.Neutral), Ui.Strong(ModelCatalog.BackendLabel(backend)), Ui.Muted(health.Detail)));
        }

        return Ui.Card(lines);
    }

    /// <summary>A ready-to-paste MCP server entry, so Claude Desktop, VS Code or Copilot can use Hubwright's read-only tools.</summary>
    private static Border McpCard()
    {
        var session = App.Current.Session;
        var cli = App.CliPath ?? "hubwright";
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["hubwright"] = new { command = cli, args = new[] { "mcp", "--tenant", session.Store.Tenant } },
            },
        }, Indented);
        var box = Ui.Area("", json, 170).With(a => a.IsReadOnly = true);
        return Ui.Card(Ui.Stack(10,
            Ui.Muted($"Add this to your MCP client's configuration (Claude Desktop: claude_desktop_config.json; VS Code: .vscode/mcp.json under \"servers\"). The tools read the {session.Store.Tenant} workspace and can only file proposals."),
            box,
            Ui.Button("Copy", () =>
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(json);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            }, glyph: Glyphs.Save)));
    }
}
