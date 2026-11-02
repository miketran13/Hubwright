using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Blueprints;
using Hubwright.Engine;
using Hubwright.Journey;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Home: the six-stage journey for the open tenant (Connect → Assess → Plan → Build → Secure → Run)
/// with the next step highlighted, then four ways in: a sample, a design from scratch, a live tenant,
/// or an offline snapshot.
/// </summary>
public static class HomePage
{
    public static UIElement Render()
    {
        var stages = JourneyStatus.For(App.Current.Session);
        var next = IntranetJourney.Next(stages);
        var done = stages.Count(s => s.State == StageState.Done);
        return Ui.Page(
            Ui.Header(Glyphs.Home, $"Your intranet: {App.Current.Session.Store.Tenant}", "Hubwright plans, builds, secures and runs your intranet. Scans only read; nothing changes in your tenant until you pick the steps and confirm them."),
            Scope(),
            Ui.Row(new ProgressBar { Maximum = stages.Count, Value = done, Width = 240 }, Ui.Muted($"{done} of {stages.Count} stages done" + (next is null ? "" : $" · next: {next.Title}"))),
            Ui.Columns(3, [.. stages.Select((s, i) => Stage(i + 1, s, ReferenceEquals(s, next)))]),
            Ui.Heading("Other ways to start"),
            Ui.Columns(2, [Sample(), Scratch(), Tenant(), Import()]));
    }

    /// <summary>What Hubwright reads and changes: four lines here, everything else in a dialog.</summary>
    private static Border Scope()
    {
        var read = Awareness.ReadAt();
        var status = Ui.Muted(read is { } at ? $"You read this on {at.ToLocalTime():yyyy-MM-dd}." : "Read this before your first Build or Secure run, and share it with the people your changes affect.");
        var stack = Ui.Stack(8, Ui.Row(Ui.Icon("lock_shield", 22), Ui.Strong("What Hubwright does in your tenant")));
        foreach (var line in Awareness.Summary)
        {
            stack.Children.Add(Ui.Text("•  " + line, 13));
        }

        Border card = null!;
        stack.Children.Add(Ui.Row(
            Ui.Button("Scope, permissions and changes in detail", async () => await ShowScopeAsync(card.XamlRoot, status), accent: read is null, glyph: Glyphs.Page),
            status));
        card = Ui.Card(stack, read is null ? Ui.Watch : Ui.Info);
        return card;
    }

    /// <summary>The full explanation, by section, with a button to record that it was read.</summary>
    internal static async Task ShowScopeAsync(XamlRoot root, TextBlock? status = null)
    {
        var body = Ui.Stack(6, Ui.Muted("The permission and operation lists below come from the code that enforces them."));
        foreach (var section in Awareness.Details())
        {
            body.Children.Add(Ui.Heading(section.Title));
            foreach (var point in section.Points)
            {
                body.Children.Add(Ui.Text("•  " + point, 13));
            }
        }

        var dialog = new ContentDialog
        {
            RequestedTheme = Ui.Theme,
            Title = "What Hubwright does in your tenant",
            Content = new ScrollViewer { Content = body, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "I've read this",
            SecondaryButtonText = "Save as Markdown",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 860.0;
        var result = await dialog.ShowAsync();
        var session = App.Current.Session;
        if (result == ContentDialogResult.Primary)
        {
            Awareness.Acknowledge(session);
            if (status is not null)
            {
                status.Text = $"You read this on {DateTime.Now:yyyy-MM-dd}.";
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            var path = Path.Combine(session.Store.Directory, "hubwright-scope.md");
            File.WriteAllText(path, Awareness.Markdown());
            Ui.Open(path);
        }
    }

    private static Border Stage(int number, JourneyStage stage, bool isNext)
    {
        var (label, color) = stage.State switch
        {
            StageState.Done => ("✓ Done", Ui.Good),
            StageState.InProgress => ("In progress", Ui.Watch),
            _ => ("To do", Ui.Neutral),
        };
        var stack = Ui.Stack(8,
            Ui.Row(Ui.Badge(label, color), Ui.Strong($"{number} · {stage.Title}")),
            Ui.Muted(stage.Purpose),
            Ui.Text(stage.Status, 13));
        if (stage.State != StageState.Done || isNext)
        {
            stack.Children.Add(Ui.Button(stage.NextAction, () => App.Current.Window.Show(stage.Page), accent: isNext));
        }

        stack.Children.Add(Ui.Muted(stage.Command));
        return Ui.Card(stack, isNext ? Ui.Brand : null);
    }

    private static Border Sample() => Ui.Card(Ui.Stack(10,
        Ui.Strong("🎲  Try a sample tenant"),
        Ui.Muted("Two made-up intranets. Fabrikam grew for years without a plan; Northwind follows Microsoft's guidance. No sign-in needed."),
        Ui.Row(
            Ui.Button("Fabrikam (messy)", () => Load("fabrikam"), accent: true),
            Ui.Button("Northwind (healthy)", () => Load("northwind")))));

    private static void Load(string name)
    {
        new TenantStore(name).SaveSnapshot(SampleTenants.ByName(name));
        App.Current.Switch(name);
        App.Current.Window.Show("home");
    }

    internal static Border Scratch()
    {
        var org = Ui.Field("Organization", "Contoso");
        var prefix = Ui.Field("Tenant prefix", "contoso", "contoso for contoso.sharepoint.com");
        var employees = new NumberBox { Header = "Employees", Value = 500, Minimum = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var departments = Ui.Field("Departments (comma-separated)", "HR, IT, Finance, Communications, Sales");
        var regions = Ui.Field("Offices or regions", "", "Seattle, London");
        var languages = Ui.Field("Languages", "en-US", "en-US, de-DE");
        var goals = new[] { "news", "policies", "onboarding", "collaboration", "communities", "copilot" }
            .Select(g => new CheckBox { Content = g, IsChecked = g is "news" or "policies" or "onboarding" }).ToList();
        var external = new CheckBox { Content = "We work with partners and guests" };
        var anyone = new CheckBox { Content = "Allow Anyone (unauthenticated) links" };
        var color = Ui.Field("Brand colour", "#0f6cbd");
        var layout = Ui.Choice("Header layout", ["standard", "compact", "minimal", "extended"], "standard");
        var goalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        goals.ForEach(goalRow.Children.Add);

        return Ui.Card(Ui.Stack(10,
            Ui.Strong("✏️  Design from scratch"),
            Ui.Muted("Answer a few questions; Hubwright drafts hubs, sites, navigation, sharing and a theme, then checks the design against every skill."),
            Ui.Columns(2, [org, prefix, employees, color]),
            departments, Ui.Columns(2, [regions, languages]),
            Ui.Muted("Goals"), goalRow,
            Ui.Row(external, anyone),
            layout,
            Ui.Row(Ui.Button("Load answers.json…", async () =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    return;
                }

                var a = System.Text.Json.JsonSerializer.Deserialize<IntranetAnswers>(await File.ReadAllTextAsync(file.Path), TenantSnapshot.Json) ?? new IntranetAnswers();
                org.Text = a.Organization;
                prefix.Text = a.TenantPrefix;
                employees.Value = a.Employees;
                departments.Text = string.Join(", ", a.Departments);
                regions.Text = string.Join(", ", a.Regions);
                languages.Text = string.Join(", ", a.Languages);
                goals.ForEach(g => g.IsChecked = a.Goals.Contains((string)g.Content, StringComparer.OrdinalIgnoreCase));
                external.IsChecked = a.ExternalCollaboration;
                anyone.IsChecked = a.AllowAnyoneLinks;
                color.Text = a.BrandColor;
                layout.SelectedItem = layout.Items.Contains(a.HeaderLayout) ? a.HeaderLayout : "standard";
            }, glyph: Glyphs.Upload),
            Ui.Button("Draft my intranet", () =>
            {
                static List<string> List(string text) => [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                var answers = new IntranetAnswers
                {
                    Organization = org.Text.Trim(),
                    TenantPrefix = prefix.Text.Trim(),
                    Employees = (int)employees.Value,
                    Departments = List(departments.Text),
                    Regions = List(regions.Text),
                    Languages = List(languages.Text),
                    Goals = [.. goals.Where(g => g.IsChecked == true).Select(g => (string)g.Content)],
                    ExternalCollaboration = external.IsChecked == true,
                    AllowAnyoneLinks = anyone.IsChecked == true,
                    BrandColor = color.Text.Trim(),
                    HeaderLayout = (string)layout.SelectedItem,
                };
                var blueprint = BlueprintGenerator.Generate(answers);
                var store = new TenantStore(answers.TenantPrefix);
                store.SaveBlueprint(blueprint);
                new AuditLog().Append("blueprint.new", blueprint.Name);
                App.Current.Switch(store.Tenant);
                App.Current.Window.Show("blueprint");
            }, accent: true, glyph: Glyphs.Sparkle))));
    }

    private static Border Tenant()
    {
        var config = App.Current.Session.Config;
        var connected = config.Tenant.ClientId is not null;
        return Ui.Card(Ui.Stack(10,
            Ui.Strong("🔎  Assess my tenant"),
            Ui.Muted("Sign in with your browser as a SharePoint Administrator. Hubwright finds your SharePoint address, scans with read-only delegated permissions, and stores the snapshot on this PC."),
            Ui.Muted(connected ? $"App {config.Tenant.ClientId}{(config.Tenant.Name is null ? "" : $" · {config.Tenant.RootUrl}")} · sign-in with {config.Tenant.SignIn}" : "First time? Setup walks you through it in four steps, starting with Hubwright's read-only app."),
            Ui.Button(connected ? "Sign in and scan" : "Connect my tenant", () => App.Current.Window.Show("setup"), accent: true, glyph: Glyphs.Shield)));
    }
    private static Border Import() => Ui.Card(Ui.Stack(10,
        Ui.Strong("📦  Import a snapshot"),
        Ui.Muted("Can't consent to an app? Run tools/collect-snapshot.ps1 with PnP PowerShell and open the JSON it writes. Everything else works the same."),
        Ui.Button("Open snapshot…", async () =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var snapshot = TenantSnapshot.FromJson(await File.ReadAllTextAsync(file.Path));
            snapshot.Source = SnapshotSource.Import;
            var store = new TenantStore(string.IsNullOrWhiteSpace(snapshot.Tenant) ? "imported" : snapshot.Tenant);
            store.SaveSnapshot(snapshot);
            new AuditLog().Append("import", file.Path);
            App.Current.Switch(store.Tenant);
            App.Current.Window.Show("home");
        }, glyph: Glyphs.Upload)));
}
