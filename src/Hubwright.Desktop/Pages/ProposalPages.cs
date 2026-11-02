using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Proposals;
using Hubwright.Storage;
using Hubwright.Templates;
using Hubwright.Theming;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// The intranet proposal for a scanned tenant: questions on the left, seeded from the scan; the
/// proposal (structure, every site's plan, new sites with templates, theme, roadmap) on the right.
/// </summary>
public sealed class ProposalPage : Grid
{
    private readonly WebView2 _web = new();
    private readonly ComboBox _saved = new() { MinWidth = 280, PlaceholderText = "No proposals yet" };
    private readonly TextBlock _status = Ui.Muted("");
    private readonly List<string> _folders;
    private readonly TextBox _org;
    private readonly NumberBox _employees;
    private readonly TextBox _departments;
    private readonly TextBox _regions;
    private readonly TextBox _languages;
    private readonly List<CheckBox> _goals;
    private readonly CheckBox _external;
    private readonly CheckBox _anyone;
    private readonly TextBox _color;
    private readonly ComboBox _layout;
    private readonly RadioButtons _approach = new() { Header = "How far should it move from today?" };
    private readonly CheckBox _packages = new() { Content = "Also suggest look book packages (needs PnP PowerShell and a SharePoint admin)", IsChecked = true };
    private readonly CheckBox _ai;
    private readonly List<(ProposalDecision Decision, Control Box)> _decisions = [];
    private readonly ProposalSeed? _seed;

    public ProposalPage()
    {
        var session = App.Current.Session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(480) });
        ColumnDefinitions.Add(new ColumnDefinition());
        _folders = [.. session.Store.IntranetProposals()];
        try
        {
            _seed = session.Workspace.Snapshot is null ? null : IntranetProposals.Seed(session);
        }
        catch (InvalidOperationException)
        {
            _seed = null;
        }

        var a = _seed?.Answers.Intranet ?? new Blueprints.IntranetAnswers();
        _org = Ui.Field("Organization", a.Organization);
        _employees = new NumberBox { Header = "Employees", Value = a.Employees, Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        _departments = Ui.Field("Functions that get a hub", string.Join(", ", a.Departments), "HR, IT, Finance");
        _regions = Ui.Field("Offices or regions", string.Join(", ", a.Regions), "EMEA, Nordics");
        _languages = Ui.Field("Languages", string.Join(", ", a.Languages));
        _goals = [.. new[] { "news", "policies", "onboarding", "collaboration", "communities", "copilot" }.Select(g => new CheckBox { Content = g, IsChecked = a.HasGoal(g), MinWidth = 0 })];
        _external = new CheckBox { Content = "We work with partners and guests", IsChecked = a.ExternalCollaboration };
        _anyone = new CheckBox { Content = "Allow Anyone (unauthenticated) links", IsChecked = a.AllowAnyoneLinks };
        _color = Ui.Field("Brand colour", a.BrandColor);
        _layout = Ui.Choice("Header layout", ["standard", "compact", "minimal", "extended"], a.HeaderLayout);
        foreach (var (approach, label) in new[] { (ProposalApproach.KeepWhatWorks, "Keep what works: fix risks, place strays, modernize"), (ProposalApproach.Balanced, "Balanced: also reshape hubs and archive long-dead sites"), (ProposalApproach.Restructure, "Restructure: design first, move everything in") })
        {
            _approach.Items.Add(new RadioButton { Content = label, Tag = approach });
        }

        _approach.SelectedIndex = 1;
        _ai = new CheckBox { Content = $"Have the AI architect write the executive summary ({session.Config.Ai.BackendFor(Roles.Architect)})" };

        Children.Add(new ScrollViewer { Content = Form(session), Padding = new Thickness(24, 16, 16, 24) });
        var right = new Grid { RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() }, Margin = new Thickness(0, 12, 12, 12), RowSpacing = 8 };
        right.Children.Add(Toolbar(session));
        SetRow(_web, 1);
        right.Children.Add(_web);
        SetColumn(right, 1);
        Children.Add(right);

        foreach (var folder in _folders)
        {
            _saved.Items.Add(Path.GetFileName(folder));
        }

        _saved.SelectionChanged += (_, _) => ShowSelected();
        Loaded += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            if (_folders.Count > 0)
            {
                _saved.SelectedIndex = 0;
            }
            else
            {
                _web.NavigateToString(Placeholder());
            }
        };
    }

    private StackPanel Form(HubwrightSession session)
    {
        var form = Ui.Stack(12, Ui.Header(Glyphs.Sparkle, "Intranet plan", "Your target intranet: structure, every site's plan, templates, theme and roadmap. From your scan, or from scratch."));
        if (session.Workspace.Blueprint is not null)
        {
            form.Children.Add(Ui.Row(Ui.Muted("This tenant also has a from-scratch design."), Ui.Button("Design check", () => App.Current.Window.Show("blueprint"), glyph: Glyphs.Blueprint)));
        }

        if (_seed is null)
        {
            form.Children.Add(Ui.InfoBar("No scan: start from scratch", "A plan from your scan keeps what works and fixes what doesn't. Without one, answer these questions and Hubwright designs the intranet; you can scan later and plan again.", InfoBarSeverity.Informational));
            form.Children.Add(HomePage.Scratch());
            form.Children.Add(Ui.Button("Scan my tenant instead", () => App.Current.Window.Show("setup"), glyph: Glyphs.Shield));
            return form;
        }

        var found = Ui.Stack(4);
        foreach (var note in _seed.Observations)
        {
            found.Children.Add(Ui.Text("• " + note, 13));
        }

        var assessment = session.Workspace.Assessment;
        form.Children.Add(Ui.Card(Ui.Stack(8,
            Ui.Strong("1 · What we found"),
            Ui.Muted(assessment is null ? "" : $"{assessment.Snapshot.Sites.Count} sites, {assessment.Snapshot.Hubs.Count} hubs, score {Ui.Score(assessment.Overall)}, {assessment.Findings.Count} findings. The answers below start from this; change anything."),
            found)));

        var goals = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 140 };
        _goals.ForEach(goals.Children.Add);
        form.Children.Add(Ui.Card(Ui.Stack(10,
            Ui.Strong("2 · Your intranet"),
            Ui.Columns(2, [_org, _employees]),
            _departments,
            Ui.Columns(2, [_regions, _languages]),
            Ui.Muted("Goals"), goals,
            _external, _anyone,
            Ui.Columns(2, [_color, _layout]),
            _approach,
            _packages)));

        var decisions = Ui.Stack(10, Ui.Strong("3 · Decisions"), Ui.Muted("Questions from the skills: decisions no scan can make. They go into the proposal's governance phase."));
        foreach (var d in IntranetProposals.Questions(session.Skills))
        {
            var choices = IntranetProposals.Choices(session.Skills, d.Id);
            Control box = choices.Count > 0
                ? new ComboBox { Header = d.Question, IsEditable = true, ItemsSource = choices.ToList(), Text = d.Answer ?? "", HorizontalAlignment = HorizontalAlignment.Stretch }
                : Ui.Field(d.Question, d.Answer ?? "", "to decide");
            ToolTipService.SetToolTip(box, d.Why);
            if (box is ComboBox combo && d.Answer is not null)
            {
                combo.SelectedItem = choices.FirstOrDefault(c => c == d.Answer);
            }

            _decisions.Add((d, box));
            decisions.Children.Add(box);
            if (d.Why is not null)
            {
                decisions.Children.Add(Ui.Muted(d.Why));
            }
        }

        form.Children.Add(Ui.Card(decisions));
        form.Children.Add(Ui.Card(Ui.Stack(10,
            Ui.Strong("4 · Create"),
            _ai,
            Ui.Row(
                Ui.Button("Create the proposal", () => CreateAsync(session), accent: true, glyph: Glyphs.Sparkle),
                Ui.Button("Load answers…", LoadAnswersAsync, glyph: Glyphs.Upload),
                Ui.Button("Save answers…", SaveAnswersAsync, glyph: Glyphs.Save)),
            _status)));
        return form;
    }

    private WrapRow Toolbar(HubwrightSession session) => Ui.Row(
        _saved,
        Ui.Button("Open in browser", () =>
        {
            if (Selected() is { } folder)
            {
                Ui.Open(Path.Combine(folder, "proposal.html"));
            }
        }, glyph: Glyphs.Open),
        Ui.Button("Folder", () =>
        {
            if (Selected() is { } folder)
            {
                Ui.Open(folder);
            }
        }, glyph: Glyphs.Open),
        Ui.Button("Use as blueprint", async () =>
        {
            if (Selected() is not { } folder)
            {
                return;
            }

            IntranetProposals.UseAsBlueprint(session, TenantStore.LoadIntranetProposal(folder));
            if (await Ui.Confirm(XamlRoot, "Blueprint saved", "The proposal's target is now this tenant's blueprint. Open it in the Preview studio, or export the provisioning scripts from Blueprint.", "Open Preview studio"))
            {
                App.Current.Window.Show("preview");
            }
        }, glyph: Glyphs.Blueprint),
        Ui.Button("Theme and logo…", () => App.Current.Window.Show("theme-editor", Selected()), glyph: Glyphs.Palette),
        Ui.Button("Build…", () =>
        {
            if (Selected() is { } folder)
            {
                App.Current.Window.Show("build", folder);
            }
        }, accent: true, glyph: Glyphs.Rocket));

    private string? Selected() => _saved.SelectedIndex >= 0 && _saved.SelectedIndex < _folders.Count ? _folders[_saved.SelectedIndex] : null;

    private void ShowSelected()
    {
        if (Selected() is { } folder && _web.CoreWebView2 is not null)
        {
            _web.Source = new Uri(IntranetProposals.Render(App.Current.Session, folder));
        }
    }

    private ProposalAnswers Answers()
    {
        static List<string> List(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        if (!Rgb.TryParse(_color.Text, out _))
        {
            throw new InvalidOperationException($"'{_color.Text}' is not a colour like #0f6cbd.");
        }

        var answers = new ProposalAnswers
        {
            Intranet = new Blueprints.IntranetAnswers
            {
                Organization = _org.Text.Trim(),
                TenantPrefix = _seed?.Answers.Intranet.TenantPrefix ?? App.Current.Session.Store.Tenant,
                Employees = (int)_employees.Value,
                Departments = List(_departments.Text),
                Regions = List(_regions.Text),
                Languages = List(_languages.Text),
                Goals = [.. _goals.Where(g => g.IsChecked == true).Select(g => (string)g.Content)],
                ExternalCollaboration = _external.IsChecked == true,
                AllowAnyoneLinks = _anyone.IsChecked == true,
                BrandColor = _color.Text.Trim(),
                HeaderLayout = (string)_layout.SelectedItem,
            },
            Approach = _approach.SelectedItem is RadioButton { Tag: ProposalApproach p } ? p : ProposalApproach.Balanced,
            UsePackages = _packages.IsChecked == true,
        };
        foreach (var (decision, box) in _decisions)
        {
            var value = box switch
            {
                ComboBox c => c.SelectedItem as string ?? c.Text,
                TextBox t => t.Text,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                answers.Decisions[decision.Id] = value.Trim();
            }
        }

        return answers;
    }

    private async Task CreateAsync(HubwrightSession session)
    {
        var answers = Answers();
        var observations = _seed?.Observations ?? [];
        var narrate = _ai.IsChecked == true;
        Activity.Report(_status.Text = "Building the proposal from the rules…");
        var proposal = await Task.Run(() => IntranetProposals.Build(session, answers, observations));
        if (narrate)
        {
            Activity.Report(_status.Text = "The architect is writing the executive summary…");
            try
            {
                proposal.Narrative = await Task.Run(() => IntranetProposals.NarrateAsync(session, proposal));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TimeoutException or IOException)
            {
                _status.Text = "The AI could not write the summary (" + ex.Message + "). The proposal is saved without it.";
            }
        }

        var folder = IntranetProposals.Save(session, proposal);
        if (!_status.Text.StartsWith("The AI could not", StringComparison.Ordinal))
        {
            _status.Text = $"Score {Ui.Score(proposal.ScoreBefore)} today → {Ui.Score(proposal.ScoreAfter)} for the target. {proposal.Blueprint.Hubs.Count} hubs, {proposal.NewSites.Count} new sites, {proposal.Sites.Count} existing sites placed.";
        }

        _folders.Insert(0, folder);
        _saved.Items.Insert(0, Path.GetFileName(folder));
        _saved.SelectedIndex = 0;
    }

    private async Task LoadAnswersAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
        picker.FileTypeFilter.Add(".json");
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        var answers = ProposalAnswers.FromJson(await File.ReadAllTextAsync(file.Path));
        var a = answers.Intranet;
        _org.Text = a.Organization;
        _employees.Value = a.Employees;
        _departments.Text = string.Join(", ", a.Departments);
        _regions.Text = string.Join(", ", a.Regions);
        _languages.Text = string.Join(", ", a.Languages);
        _goals.ForEach(g => g.IsChecked = a.HasGoal((string)g.Content));
        _external.IsChecked = a.ExternalCollaboration;
        _anyone.IsChecked = a.AllowAnyoneLinks;
        _color.Text = a.BrandColor;
        _layout.SelectedItem = _layout.Items.Contains(a.HeaderLayout) ? a.HeaderLayout : "standard";
        _approach.SelectedIndex = (int)answers.Approach;
        _packages.IsChecked = answers.UsePackages;
        foreach (var (decision, box) in _decisions)
        {
            if (answers.Decisions.TryGetValue(decision.Id, out var value))
            {
                if (box is ComboBox c)
                {
                    c.Text = value;
                }
                else if (box is TextBox t)
                {
                    t.Text = value;
                }
            }
        }
    }

    private async Task SaveAnswersAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "proposal-answers" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
        picker.FileTypeChoices.Add("Answers", [".json"]);
        if (await picker.PickSaveFileAsync() is { } file)
        {
            await File.WriteAllTextAsync(file.Path, Answers().ToJson());
        }
    }

    private static string Placeholder() => """
        <html><body style="font-family:Segoe UI,system-ui,sans-serif;color:#605e5c;display:grid;place-items:center;height:90vh;margin:0">
        <div style="text-align:center;max-width:440px"><div style="font-size:44px">🏗️</div><h2 style="color:#323130">Your proposal appears here</h2>
        <p>Answer the questions on the left and select <b>Create the proposal</b>. You get the target structure, a plan for every existing site, new sites with SharePoint templates, the theme and a phased roadmap, all checked against the skills.</p></div></body></html>
        """;
}

/// <summary>Microsoft's built-in site templates and the look book packages, sketched in your theme, with suggestions for a site.</summary>
public sealed class TemplatesPage : Grid
{
    private readonly WebView2 _web = new();
    private readonly ComboBox _source = Ui.Choice("Show", ["All", "Built into SharePoint", "Look book packages"], "All");
    private readonly TextBox _title = Ui.Field("Suggest for a site titled", "", "HR – Benefits");
    private readonly TextBox _purpose = Ui.Field("What it's for (optional)", "", "benefits, pay and policies for employees");
    private readonly StackPanel _matches = Ui.Stack(8);
    private string? _highlight;

    public TemplatesPage()
    {
        var session = App.Current.Session;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var catalog = session.Skills.Templates;
        var panel = Ui.Stack(12,
            Ui.Header(Glyphs.Library, "Site templates", $"{catalog.Count(t => t.Source == TemplateSource.Microsoft)} built into SharePoint, {catalog.Count(t => t.Source == TemplateSource.PnpPackage)} look book packages."),
            Ui.Muted(TemplateGallery.Disclaimer),
            _source,
            _title, _purpose,
            Ui.Button("Suggest", Suggest, accent: true, glyph: Glyphs.Sparkle),
            _matches,
            Ui.Button("Open the gallery in the browser", () =>
            {
                var path = Path.Combine(session.Store.Directory, "templates.html");
                File.WriteAllText(path, Gallery());
                Ui.Open(path);
            }, glyph: Glyphs.Open),
            Ui.Button("Edit the catalogue (templates.yaml)", () =>
            {
                if (session.Skills.Find("site-templates") is { } skill)
                {
                    Ui.Open(skill.Directory);
                }
            }, glyph: Glyphs.Open));
        Children.Add(new ScrollViewer { Content = panel, Padding = new Thickness(24, 16, 16, 24) });
        SetColumn(_web, 1);
        _web.Margin = new Thickness(0, 12, 12, 12);
        Children.Add(_web);
        _source.SelectionChanged += (_, _) => Render();
        _title.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Suggest();
            }
        };
        Loaded += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            Render();
        };
    }

    private IEnumerable<SiteTemplate> Filtered() => App.Current.Session.Skills.Templates.Where(t => (string)_source.SelectedItem switch
    {
        "Built into SharePoint" => t.Source == TemplateSource.Microsoft,
        "Look book packages" => t.Source == TemplateSource.PnpPackage,
        _ => true,
    });

    private string Gallery()
    {
        var session = App.Current.Session;
        var theme = session.Workspace.Blueprint?.Theme() ?? ThemeGenerator.Generate(new ThemeInput("Preview", Rgb.Parse("#0f6cbd")));
        return TemplateGallery.Render(Filtered(), theme, _highlight);
    }

    private void Render()
    {
        if (_web.CoreWebView2 is not null)
        {
            _web.NavigateToString(Gallery());
        }
    }

    private void Suggest()
    {
        _matches.Children.Clear();
        if (string.IsNullOrWhiteSpace(_title.Text))
        {
            return;
        }

        var matches = TemplateMatcher.Suggest(Filtered(), _title.Text, _purpose.Text);
        if (matches.Count == 0)
        {
            _matches.Children.Add(Ui.Muted("Nothing specific fits: use Standard communication or Standard team."));
        }

        foreach (var m in matches)
        {
            _matches.Children.Add(Ui.Card(Ui.Stack(4,
                Ui.Row(Ui.Strong(m.Template.Name), Ui.Badge(m.Template.Source == TemplateSource.Microsoft ? "built in" : "package", m.Template.Source == TemplateSource.Microsoft ? Ui.Good : Ui.Info)),
                Ui.Muted("Matched: " + string.Join(", ", m.Matched)),
                Ui.Text(m.Template.HowToApply, 12))));
        }

        _highlight = matches.FirstOrDefault()?.Template.Id;
        Render();
        if (_highlight is not null && _web.CoreWebView2 is not null)
        {
            var id = _highlight;
            _web.NavigationCompleted += Scroll;
            void Scroll(WebView2 sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs args)
            {
                sender.NavigationCompleted -= Scroll;
                _ = sender.ExecuteScriptAsync($"document.getElementById('{id}')?.scrollIntoView({{behavior:'smooth',block:'center'}})");
            }
        }
    }
}
