using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Reports;
using Hubwright.Rules;
using Hubwright.Snapshot;

namespace Hubwright.Desktop.Pages;

/// <summary>Scores, the worst problems and what could not be read.</summary>
public static class DashboardPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var assessment = session.Workspace.Assessment;
        if (assessment is null)
        {
            return Ui.Page(Ui.Header(Glyphs.Dashboard, "Dashboard"), Ui.InfoBar("No snapshot yet", "Load a sample, scan your tenant or import a snapshot on the Home page.", InfoBarSeverity.Warning), Ui.Button("Go to Home", () => App.Current.Window.Show("home"), accent: true));
        }

        var s = assessment.Snapshot;
        var tiles = new List<UIElement>
        {
            Ui.Tile("Overall", Ui.Score(assessment.Overall), Ui.ScoreColor(assessment.Overall), $"grade {Scoring.Assessment.Grade(assessment.Overall)}"),
            Ui.Tile("Findings", assessment.Findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), assessment.Findings.Any(f => f.Severity >= Severity.High) ? Ui.Problem : Ui.Watch, $"{assessment.Findings.Count(f => f.Severity >= Severity.High)} high or critical"),
            Ui.Tile("Sites", s.Sites.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Ui.Info, $"{s.Hubs.Count} hubs · home site: {s.HomeSite?.Title ?? "none"}"),
            Ui.Tile("Not read", s.ScanIssues.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), SiteAccess.Unread(s).Count > 0 ? Ui.Watch : s.ScanIssues.Count == 0 ? Ui.Good : Ui.Neutral, SiteAccess.Unread(s).Count is var locked and > 0 ? $"{locked} site(s) you lack permissions on · left out of the score" : "left out of the score"),
        };

        var categories = assessment.Categories.Select(c => (UIElement)CategoryCard(c)).ToList();

        var top = Ui.Stack(8);
        foreach (var group in assessment.Findings.GroupBy(f => f.RuleId).Take(8))
        {
            var f = group.First();
            top.Children.Add(Ui.Card(Ui.Row(Ui.Badge(f.Severity.ToString(), Ui.SeverityColor(f.Severity)), Ui.Strong(f.Title), Ui.Muted($"{group.Count()} target(s) · {f.Category}"))));
        }

        var page = Ui.Page(
            Ui.Header(Glyphs.Dashboard, s.Tenant, $"Snapshot {s.CapturedAt:yyyy-MM-dd HH:mm} UTC · {s.Source.ToString().ToLowerInvariant()}{(s.Scope is null ? "" : " · " + s.Scope)}"),
            Ui.Columns(4, tiles),
            Ui.Row(
                Ui.Button("Open HTML report", () =>
                {
                    var path = session.Store.SaveReport($"assessment-{DateTime.UtcNow:yyyyMMdd-HHmm}.html", ReportWriter.Html(assessment));
                    Ui.Open(path);
                }, glyph: Glyphs.Open),
                Ui.Button("Start an intranet proposal", () => App.Current.Window.Show("proposal"), accent: true, glyph: Glyphs.Sparkle),
                Ui.Button("Write the 30/60/90 plan with AI", () => App.Current.Window.Show("ask"), glyph: Glyphs.Chat),
                Ui.Button("Preview a new look", () => App.Current.Window.Show("preview"), glyph: Glyphs.Palette)),
            Ui.Heading("Scores"),
            Ui.Columns(4, categories),
            Ui.Heading("Worst findings"),
            top);

        if (session.Store.IntranetProposals().Count == 0)
        {
            var next = Ui.InfoBar("Next: an intranet proposal", "Hubwright can turn this scan into a target intranet: hubs and sites, a plan for every existing site, SharePoint templates for new ones, the theme and a roadmap. It asks a few questions first.", InfoBarSeverity.Success);
            next.ActionButton = Ui.Button("Start the proposal", () => App.Current.Window.Show("proposal"), accent: true);
            ((StackPanel)page.Content).Children.Insert(1, next);
        }

        if (s.Source == SnapshotSource.Sample)
        {
            ((StackPanel)page.Content).Children.Insert(1, Ui.InfoBar("Sample data", "This is a made-up tenant for exploring Hubwright.", InfoBarSeverity.Warning));
        }

        return AccessWarning.Insert(page, s);
    }

    private static Border CategoryCard(Scoring.CategoryScore c)
    {
        var card = Ui.Card(Ui.Stack(4,
            Ui.Muted(c.Category),
            Ui.Row(new TextBlock { Text = Ui.Score(c.Score), FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ui.Brush(Ui.ScoreColor(c.Score)) }, Ui.Muted(c.Grade)),
            new ProgressBar { Value = c.Score ?? 0, Maximum = 100 },
            Ui.Muted($"{c.Failed} of {c.Rules} rules failed" + (c.NotAssessed > 0 ? $" · {c.NotAssessed} not assessed" : ""))));
        card.Tapped += (_, _) => App.Current.Window.Show("findings", c.Category);
        return card;
    }
}

/// <summary>Every finding, filterable, with the evidence behind it.</summary>
public static class FindingsPage
{
    /// <summary>One expander per rule, with every target, the fix and the quote it enforces.</summary>
    public static IEnumerable<Expander> Groups(IEnumerable<Finding> findings)
    {
        foreach (var group in findings.GroupBy(f => f.RuleId))
        {
            var f = group.First();
            var body = Ui.Stack(6);
            foreach (var item in group.Take(40))
            {
                body.Children.Add(Ui.Text("• " + item.Message + (item.Target == "tenant" ? "" : "   " + item.Target), 13));
            }

            if (f.Fix is not null)
            {
                body.Children.Add(Ui.Text("Fix: " + f.Fix));
            }

            if (f.Source is not null)
            {
                body.Children.Add(Ui.Card(Ui.Stack(4, Ui.Text($"“{f.Quote}”", 13), Ui.Link(f.Source, f.Source), Ui.Muted($"reviewed {f.Reviewed:yyyy-MM-dd} · skill {f.SkillId}"))));
            }

            yield return new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Header = Ui.Row(Ui.Badge(f.Severity.ToString(), Ui.SeverityColor(f.Severity)), Ui.Strong(f.Title), Ui.Muted($"{group.Count()} · {ReportWriter.BasisLabel(f.Basis)} · {f.RuleId}")),
                Content = body,
            };
        }
    }

    public static UIElement Render(string? category)
    {
        var assessment = App.Current.Session.Workspace.Assessment;
        if (assessment is null)
        {
            return DashboardPage.Render();
        }

        var categories = Ui.Choice("Category", ["All", .. assessment.Categories.Select(c => c.Category)], category ?? "All");
        var severities = Ui.Choice("At least", ["info", "low", "medium", "high", "critical"], "info");
        var list = Ui.Stack(10);

        void Fill()
        {
            list.Children.Clear();
            var cat = (string)categories.SelectedItem;
            var min = Enum.Parse<Severity>((string)severities.SelectedItem, true);
            foreach (var item in Groups(assessment.Findings.Where(f => f.Severity >= min && (cat == "All" || f.Category == cat))))
            {
                list.Children.Add(item);
            }

            var advisories = assessment.Advisories.ToList();
            if (advisories.Count > 0 && cat == "All")
            {
                list.Children.Add(Ui.Heading("To confirm by hand"));
                foreach (var r in advisories)
                {
                    list.Children.Add(Ui.Card(Ui.Stack(4, Ui.Strong(r.Rule.Title), Ui.Muted(r.Rule.Fix ?? ""), r.Rule.Source is null ? new TextBlock() : Ui.Link(r.Rule.Source, r.Rule.Source))));
                }
            }

            if (list.Children.Count == 0)
            {
                list.Children.Add(Ui.InfoBar("Nothing here", "No findings match these filters. 🎉", InfoBarSeverity.Success));
            }
        }

        categories.SelectionChanged += (_, _) => Fill();
        severities.SelectionChanged += (_, _) => Fill();
        Fill();
        return AccessWarning.Insert(Ui.Page(Ui.Header(Glyphs.Warning, "Findings", "Each finding shows the rule's basis: Microsoft-documented (with the quote it enforces) or a heuristic you can tune in overrides.yaml."), Ui.Row(categories, severities), list), assessment.Snapshot);
    }
}

/// <summary>The tenant's hub families next to the blueprint's.</summary>
public static class StructurePage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var current = session.Workspace.Snapshot;
        var target = session.Workspace.Blueprint?.ToSnapshot();
        return AccessWarning.Insert(Ui.Page(
            Ui.Header(Glyphs.Tree, "Structure", "Hubs and their sites today, next to the blueprint. Sites in no hub are listed at the end; ⚠ marks a site you can't open, and those whose hub is unknown are listed apart."),
            Ui.Columns(2, [Tree("Today", current), Tree("Blueprint", target)])), current);
    }

    private static Border Tree(string title, TenantSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return Ui.Card(Ui.Stack(6, Ui.Strong(title), Ui.Muted(title == "Today" ? "No snapshot yet." : "No blueprint yet: draft one on the Home page.")));
        }

        var tree = new TreeView { SelectionMode = TreeViewSelectionMode.None, MaxHeight = 640 };
        var home = snapshot.HomeSite;
        if (home is not null)
        {
            tree.RootNodes.Add(new TreeViewNode { Content = $"🏠 {home.Title} (home site)" });
        }

        foreach (var hub in snapshot.Hubs.Where(h => h.ParentHubId is null || snapshot.Hubs.All(p => p.Id != h.ParentHubId)))
        {
            tree.RootNodes.Add(Hub(snapshot, hub));
        }

        // A site the scan couldn't open, with no hub read, is not "in no hub": its hub is unknown.
        var unknown = snapshot.Sites.Where(s => s.HubId is null && !s.IsHomeSite && s.Readable == false && snapshot.Unread(ScanAreas.Hubs, s.Url)).ToList();
        var orphans = snapshot.Sites.Where(s => s.HubId is null && !s.IsHomeSite).Except(unknown).ToList();
        if (orphans.Count > 0)
        {
            var node = new TreeViewNode { Content = $"⚪ In no hub ({orphans.Count})" };
            foreach (var site in orphans.Take(200))
            {
                node.Children.Add(new TreeViewNode { Content = Label(site) });
            }

            tree.RootNodes.Add(node);
        }

        if (unknown.Count > 0)
        {
            var node = new TreeViewNode { Content = $"⚠ You can't open these; hub unknown ({unknown.Count})" };
            foreach (var site in unknown.Take(200))
            {
                node.Children.Add(new TreeViewNode { Content = Label(site) });
            }

            tree.RootNodes.Add(node);
        }

        return Ui.Card(Ui.Stack(8, Ui.Strong($"{title}: {snapshot.Hubs.Count} hubs, {snapshot.Sites.Count} sites"), tree));
    }

    private static TreeViewNode Hub(TenantSnapshot snapshot, HubInfo hub)
    {
        var node = new TreeViewNode { Content = $"🧭 {hub.Title}", IsExpanded = true };
        foreach (var child in snapshot.Hubs.Where(h => h.ParentHubId == hub.Id))
        {
            node.Children.Add(Hub(snapshot, child));
        }

        foreach (var site in snapshot.Sites.Where(s => s.HubId == hub.Id && !s.IsHub))
        {
            node.Children.Add(new TreeViewNode { Content = Label(site) });
        }

        return node;
    }

    private static string Label(SiteInfo site)
        => $"{(site.Kind == SiteKind.Communication ? "📄" : site.Kind == SiteKind.Classic ? "🏚" : "👥")} {site.Title}{(site.IsPublic == false ? " 🔒" : "")}{(site.IsExtranet ? " (extranet)" : "")}{(site.Readable == false ? "  ⚠ can't open" : "")}{(site.SubsiteCount > 0 ? $" · {site.SubsiteCount} subsites" : "")}";
}
