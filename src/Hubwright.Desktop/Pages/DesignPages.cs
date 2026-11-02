using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Engine;
using Hubwright.Preview;
using Hubwright.Theming;

namespace Hubwright.Desktop.Pages;

/// <summary>The target design: why it looks the way it does, how it scores, and its exports.</summary>
public static class BlueprintPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var blueprint = session.Workspace.Blueprint;
        if (blueprint is null)
        {
            return Ui.Page(Ui.Header(Glyphs.Blueprint, "Blueprint"), Ui.InfoBar("No blueprint for this tenant", "Draft one from the questionnaire on the Home page.", InfoBarSeverity.Informational), Ui.Button("Go to Home", () => App.Current.Window.Show("home"), accent: true));
        }

        var check = session.Workspace.Assess(blueprint.ToSnapshot());
        var rationale = Ui.Stack(4);
        foreach (var line in blueprint.Rationale)
        {
            rationale.Children.Add(Ui.Text("• " + line, 13));
        }

        var hubs = blueprint.Hubs.Select(h => (UIElement)Ui.Card(Ui.Stack(4,
            Ui.Strong("🧭 " + h.Title),
            Ui.Muted(h.Purpose),
            Ui.Text(string.Join("\n", h.Sites.Select(s => $"  {(s.Private ? "🔒" : "📄")} {s.Title}")), 13)))).ToList();
        var issues = Ui.Stack(4);
        foreach (var f in check.Findings)
        {
            issues.Children.Add(Ui.Row(Ui.Badge(f.Severity.ToString(), Ui.SeverityColor(f.Severity)), Ui.Text(f.Message, 13)));
        }

        var status = Ui.Muted("");
        return Ui.Page(
            Ui.Header(Glyphs.Blueprint, blueprint.Name, $"{blueprint.Hubs.Count} hubs · {blueprint.Hubs.Sum(h => h.Sites.Count) + blueprint.Standalone.Count + 1} sites · checked against every skill: {Ui.Score(check.Overall)}"),
            Ui.Row(
                Ui.Button("Preview it", () => App.Current.Window.Show("preview"), accent: true, glyph: Glyphs.Palette),
                Ui.Button("Export scripts", () => Ui.Open(Export(Path.Combine(session.Store.Directory, "export"))), glyph: Glyphs.Download),
                Ui.Button("Export to folder…", async () =>
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker();
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
                    picker.FileTypeFilter.Add("*");
                    if (await picker.PickSingleFolderAsync() is { } folder)
                    {
                        Ui.Open(Export(folder.Path));
                    }
                }, glyph: Glyphs.Download),
                Ui.Button("Ask the architect to improve it", async () =>
                {
                    status.Text = "The architect is reviewing the blueprint…";
                    var answer = await Task.Run(() => session.Runner.RunAsync(Agents.Roles.Architect, "Review the latest blueprint with check_blueprint and get_blueprint. Suggest improvements; if they are substantial, file them with propose_blueprint."));
                    status.Text = "";
                    await Ui.Message(App.Current.Window.Content.XamlRoot, "Architect", answer);
                }, glyph: Glyphs.Sparkle)),
            status,
            Ui.Heading("Why this shape"), Ui.Card(rationale),
            Ui.Heading("Hubs"), Ui.Columns(3, hubs),
            Ui.Heading("Checks"), check.Findings.Count == 0 ? Ui.InfoBar("Clean", "The blueprint passes every rule that can be checked before it is built.", InfoBarSeverity.Success) : Ui.Card(issues),
            new Expander { Header = "Blueprint JSON", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Ui.Area("", blueprint.ToJson(), 360).With(a => a.IsReadOnly = true) },
            new Expander { Header = "Check report (Markdown)", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = Ui.Area("", Reports.ReportWriter.Markdown(check), 360).With(a => a.IsReadOnly = true) });

        string Export(string folder)
        {
            var dir = Directory.CreateDirectory(folder).FullName;
            File.WriteAllText(Path.Combine(dir, "provision.ps1"), Exporters.ProvisioningScript(blueprint));
            File.WriteAllText(Path.Combine(dir, "site-script.json"), Exporters.SiteScript(blueprint));
            File.WriteAllText(Path.Combine(dir, "theme.ps1"), Exporters.ThemeScript(blueprint.Theme()));
            File.WriteAllText(Path.Combine(dir, "theme.json"), blueprint.Theme().ToJson());
            File.WriteAllText(Path.Combine(dir, "mockup.html"), MockupRenderer.Render(MockupModel.FromBlueprint(blueprint)));
            session.Audit.Append("blueprint.export", dir);
            return dir;
        }
    }
}

/// <summary>
/// Theme and page mockup side by side: pick a brand colour and header layout, see the home page,
/// structure and contrast checks in a WebView2, then save the theme or its PowerShell.
/// </summary>
public sealed class PreviewPage : Grid
{
    private readonly WebView2 _web = new();
    private readonly TextBox _color = Ui.Field("Brand colour", "#0f6cbd");
    private readonly ColorPicker _picker = new() { IsAlphaEnabled = false, IsMoreButtonVisible = false, ColorSpectrumShape = ColorSpectrumShape.Ring, IsColorChannelTextInputVisible = false, IsHexInputVisible = false };
    private readonly ComboBox _layout = Ui.Choice("Header layout", ["standard", "compact", "minimal", "extended"], "standard");
    private readonly ComboBox _source = Ui.Choice("Structure from", ["blueprint", "snapshot"], "blueprint");
    private readonly TextBox _background = Ui.Field("Background", "#ffffff");
    private readonly TextBox _name = Ui.Field("Theme name", "");
    private readonly InfoBar _imported = new() { Severity = InfoBarSeverity.Informational, IsClosable = true, IsOpen = false };
    private readonly StackPanel _checks = Ui.Stack(4);
    private SharePointTheme? _theme;
    private SharePointTheme? _loaded;
    private bool _updating;

    public PreviewPage()
    {
        var session = App.Current.Session;
        var blueprint = session.Workspace.Blueprint;
        if (blueprint is null)
        {
            _source.SelectedItem = "snapshot";
        }
        else
        {
            _color.Text = blueprint.BrandColor;
            _layout.SelectedItem = blueprint.HeaderLayout;
        }

        _name.Text = $"{(blueprint?.Organization ?? session.Store.Tenant)} brand";

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var panel = Ui.Stack(12,
            Ui.Header(Glyphs.Palette, "Preview studio"),
            Ui.Muted("A generic SharePoint-like mockup for stakeholders. Contrast is checked against WCAG 2.1 AA."),
            _picker, Ui.Columns(2, [_color, _background]), _name, _layout, _source,
            Ui.Row(
                Ui.Button("Make accessible", MakeAccessible, glyph: Glyphs.Check),
                Ui.Button("Save theme…", SaveAsync, glyph: Glyphs.Save)),
            Ui.Button("Check a theme file…", LoadAsync, glyph: Glyphs.Upload),
            _imported,
            Ui.Button("Open in browser", () =>
            {
                var path = Path.Combine(session.Store.Directory, "mockup.html");
                File.WriteAllText(path, MockupRenderer.Render(Model()));
                Ui.Open(path);
            }, glyph: Glyphs.Open),
            Ui.Heading("Contrast"), _checks);
        var left = new ScrollViewer { Content = panel, Padding = new Thickness(20, 16, 16, 16) };
        Children.Add(left);
        SetColumn(_web, 1);
        _web.Margin = new Thickness(0, 0, 8, 8);
        Children.Add(_web);

        _color.TextChanged += (_, _) => Edited();
        _background.TextChanged += (_, _) => Edited();
        _name.TextChanged += (_, _) => Edited();
        _imported.Closed += (_, _) => Edited();
        _picker.ColorChanged += (_, _) => Update(fromText: false);
        _layout.SelectionChanged += (_, _) => Update(fromText: true);
        _source.SelectionChanged += (_, _) => Update(fromText: true);
        Loaded += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            Update(fromText: true);
        };
    }

    /// <summary>Typing a colour goes back to a generated theme.</summary>
    private void Edited()
    {
        if (!_updating)
        {
            _loaded = null;
            _imported.IsOpen = false;
        }

        Update(fromText: true);
    }

    private void Update(bool fromText)
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            if (fromText && Rgb.TryParse(_color.Text, out var typed))
            {
                _picker.Color = Windows.UI.Color.FromArgb(255, typed.R, typed.G, typed.B);
            }
            else if (!fromText)
            {
                _color.Text = new Rgb(_picker.Color.R, _picker.Color.G, _picker.Color.B).Hex;
            }

            if (_loaded is null && (!Rgb.TryParse(_color.Text, out _) || !Rgb.TryParse(_background.Text, out _)))
            {
                return;
            }

            var model = Model();
            _theme = model.Theme;
            _checks.Children.Clear();
            foreach (var c in model.Theme.Checks)
            {
                _checks.Children.Add(Ui.Row(Ui.Badge(c.Passes ? "pass" : "fail", c.Passes ? Ui.Good : Ui.Problem), Ui.Text($"{c.Usage}: {c.Ratio:0.00}:1", 12)));
            }

            if (_web.CoreWebView2 is not null)
            {
                _web.NavigateToString(MockupRenderer.Render(model));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private MockupModel Model()
    {
        var session = App.Current.Session;
        var layout = (string)_layout.SelectedItem;
        var theme = _loaded ?? Generated();
        var blueprint = session.Workspace.Blueprint;
        if ((string)_source.SelectedItem == "blueprint" && blueprint is not null)
        {
            if (_loaded is null)
            {
                blueprint.BrandColor = Rgb.Parse(_color.Text).Hex;
            }

            blueprint.HeaderLayout = layout;
            return MockupModel.FromBlueprint(blueprint).WithTheme(theme);
        }

        var snapshot = session.Workspace.Snapshot ?? new Snapshot.TenantSnapshot { Tenant = session.Store.Tenant };
        return MockupModel.FromSnapshot(snapshot, theme, layout);
    }

    private SharePointTheme Generated()
    {
        var background = Rgb.Parse(_background.Text);
        return ThemeGenerator.Generate(new ThemeInput(string.IsNullOrWhiteSpace(_name.Text) ? "Hubwright theme" : _name.Text.Trim(), Rgb.Parse(_color.Text))
        {
            Background = background,
            Text = background.IsDark ? new Rgb(0xf3, 0xf2, 0xf1) : new Rgb(0x32, 0x31, 0x30),
        });
    }

    private async Task LoadAsync()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _loaded = ThemeGenerator.FromJson(await File.ReadAllTextAsync(file.Path), Path.GetFileNameWithoutExtension(file.Path));
        var failed = _loaded.Checks.Count(c => !c.Passes);
        _imported.Title = _loaded.Name;
        _imported.Message = failed == 0 ? "Every text pair passes WCAG 2.1 AA. Close this to go back to the colour picker." : $"{failed} text pair(s) fail WCAG 2.1 AA. Close this to go back to the colour picker.";
        _imported.Severity = failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        _updating = true;
        _imported.IsOpen = true;
        _updating = false;
        Update(fromText: true);
    }

    private void MakeAccessible()
    {
        if (Rgb.TryParse(_color.Text, out var color) && ThemeGenerator.SuggestAccessiblePrimary(color, Rgb.TryParse(_background.Text, out var background) ? background : Rgb.White) is { } better)
        {
            _color.Text = better.Hex;
        }
    }

    private async Task SaveAsync()
    {
        if (_theme is null)
        {
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "theme" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
        picker.FileTypeChoices.Add("Theme JSON", [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await File.WriteAllTextAsync(file.Path, _theme.ToJson());
        await File.WriteAllTextAsync(Path.ChangeExtension(file.Path, ".ps1"), _theme.ToPowerShell(hideDefaultThemes: true));
        App.Current.Session.Audit.Append("theme.save", file.Path);
    }
}
