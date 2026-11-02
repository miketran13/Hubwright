using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Reports;
using Hubwright.Snapshot;

namespace Hubwright.Desktop.Pages;

/// <summary>Every snapshot of the tenant with its score, and what changed between any two.</summary>
public static class HistoryPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var files = session.Store.Snapshots().Take(24).ToList();
        if (files.Count == 0)
        {
            return Ui.Page(Ui.Header(Glyphs.History, "History"), Ui.InfoBar("No snapshots yet", "Scan the tenant, import a snapshot or load a sample on the Home page.", InfoBarSeverity.Informational));
        }

        var labels = new List<string>();
        var rows = Ui.Stack(6);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            labels.Add(name);
            try
            {
                var snapshot = TenantSnapshot.FromJson(File.ReadAllText(file));
                var assessment = session.Workspace.Assess(snapshot);
                rows.Children.Add(Ui.Card(Ui.Row(
                    Ui.Badge(Ui.Score(assessment.Overall), Ui.ScoreColor(assessment.Overall)),
                    Ui.Strong($"{snapshot.CapturedAt:yyyy-MM-dd HH:mm} UTC").With(t => t.MinWidth = 170),
                    Ui.Muted($"{snapshot.Source.ToString().ToLowerInvariant()} · {snapshot.Sites.Count} sites · {snapshot.Hubs.Count} hubs · {assessment.Findings.Count} findings{(snapshot.Scope is null ? "" : " · " + snapshot.Scope)}"),
                    Ui.Button("Open file", () => Ui.Open(file), glyph: Glyphs.Open))));
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                rows.Children.Add(Ui.InfoBar(name, "Could not read: " + ex.Message, InfoBarSeverity.Warning));
            }
        }

        var before = Ui.Choice("Before", labels, labels.Count > 1 ? labels[1] : labels[0]);
        var after = Ui.Choice("After", labels, labels[0]);
        var result = Ui.Stack(6);
        void Compare()
        {
            result.Children.Clear();
            if (before.SelectedItem is not string a || after.SelectedItem is not string b)
            {
                return;
            }

            TenantSnapshot Load(string label) => TenantSnapshot.FromJson(File.ReadAllText(files[labels.IndexOf(label)]));
            var diff = AssessmentDiff.Between(session.Workspace.Assess(Load(a)), session.Workspace.Assess(Load(b)));
            result.Children.Add(Ui.Columns(3,
            [
                Ui.Tile("Overall", $"{Ui.Score(diff.OverallBefore)} → {Ui.Score(diff.OverallAfter)}", Ui.ScoreColor(diff.OverallAfter)),
                Ui.Tile("New findings", diff.NewFindings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), diff.NewFindings.Count == 0 ? Ui.Good : Ui.Problem),
                Ui.Tile("Resolved", diff.ResolvedFindings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), Ui.Good),
            ]));
            result.Children.Add(Ui.Card(Ui.Text(diff.ToMarkdown(), 13)));
        }

        before.SelectionChanged += (_, _) => Compare();
        after.SelectionChanged += (_, _) => Compare();
        Compare();
        return Ui.Page(
            Ui.Header(Glyphs.History, "History", "Scan regularly and watch the score move. Each snapshot is a JSON file on this PC."),
            rows,
            Ui.Heading("Compare two snapshots"),
            Ui.Row(before, after),
            result);
    }
}

/// <summary>Write the assessment as Markdown, HTML or JSON, optionally with the assessor's plan.</summary>
public static class ReportsPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var assessment = session.Workspace.Assessment;
        if (assessment is null)
        {
            return DashboardPage.Render();
        }

        var format = Ui.Choice("Format", ["html", "md", "json"], "html");
        var ai = new CheckBox { Content = $"Add the assessor's summary and 30/60/90-day plan (AI: {session.Config.Ai.BackendFor(Roles.Assessor)})" };
        var status = Ui.Muted("");
        var existing = Ui.Stack(6);
        foreach (var file in Directory.GetFiles(session.Store.ReportsDirectory).OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
        {
            existing.Children.Add(Ui.Row(Ui.Glyph(Glyphs.Page), Ui.Text(Path.GetFileName(file), 13), Ui.Muted($"{File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}"), Ui.Button("Open", () => Ui.Open(file))));
        }

        async Task<string> WriteAsync()
        {
            var chosen = (string)format.SelectedItem;
            string? narrative = null;
            if (ai.IsChecked == true)
            {
                status.Text = "The assessor is writing the plan…";
                narrative = await Task.Run(() => session.Runner.RunAsync(Roles.Assessor, "Write the assessment summary and the 30/60/90-day plan for this tenant."));
            }

            status.Text = "";
            var kind = chosen switch { "md" => ReportFormat.Markdown, "json" => ReportFormat.Json, _ => ReportFormat.Html };
            return ReportWriter.Write(assessment, kind, narrative);
        }

        return AccessWarning.Insert(Ui.Page(
            Ui.Header(Glyphs.Page, "Reports", "The same report as `hubwright assess`: scores, findings with their sources, the sites you can't open, and what could not be assessed."),
            Ui.Card(Ui.Stack(10, format, ai,
                Ui.Row(
                    Ui.Button("Write and open", async () =>
                    {
                        var content = await WriteAsync();
                        var path = session.Store.SaveReport($"assessment-{DateTime.UtcNow:yyyyMMdd-HHmm}.{format.SelectedItem}", content);
                        session.Audit.Append("report", path);
                        Ui.Open(path);
                        App.Current.Window.Show("reports");
                    }, accent: true, glyph: Glyphs.Open),
                    Ui.Button("Save as…", async () =>
                    {
                        var ext = "." + (string)format.SelectedItem;
                        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{session.Store.Tenant}-assessment" };
                        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
                        picker.FileTypeChoices.Add("Report", [ext]);
                        var file = await picker.PickSaveFileAsync();
                        if (file is not null)
                        {
                            await File.WriteAllTextAsync(file.Path, await WriteAsync());
                            session.Audit.Append("report", file.Path);
                        }
                    }, glyph: Glyphs.Save),
                    Ui.Button("Reports folder", () => Ui.Open(session.Store.ReportsDirectory), glyph: Glyphs.Open),
                    Ui.Button("Findings to Excel (CSV)…", async () => await SaveAs.TextAsync($"{session.Store.Tenant}-findings", "CSV", ".csv", Monitoring.CsvExport.Findings(assessment)), glyph: Glyphs.Save)),
                status)),
            Ui.Heading("Written so far"),
            existing.Children.Count == 0 ? Ui.Muted("None yet.") : existing), assessment.Snapshot);
    }
}

/// <summary>The hash-chained log of everything Hubwright did on this PC, and a check that nobody edited it.</summary>
public static class AuditPage
{
    public static UIElement Render()
    {
        var log = new Storage.AuditLog();
        var entries = log.Read();
        var verdict = Ui.Muted("");
        var list = Ui.Stack(4);
        foreach (var e in entries.Reverse().Take(200))
        {
            list.Children.Add(Ui.Row(
                Ui.Muted($"{e.At.ToLocalTime():yyyy-MM-dd HH:mm}").With(t => t.MinWidth = 120),
                Ui.Badge(e.Action, Ui.Brand),
                Ui.Muted(e.Actor),
                Ui.Text(e.Detail, 13)));
        }

        return Ui.Page(
            Ui.Header(Glyphs.Shield, "Audit log", "Sign-ins, scans, exports and proposal decisions. Each entry carries the hash of the one before."),
            Ui.Row(Ui.Button("Verify the chain", () =>
            {
                var broken = log.Verify();
                verdict.Text = broken < 0 ? $"✓ Intact: {entries.Count} entries." : $"✗ Broken at entry {broken + 1}: the log was edited after it was written.";
            }, accent: true, glyph: Glyphs.Check), verdict),
            entries.Count == 0 ? Ui.Muted("Nothing logged yet.") : list);
    }
}

/// <summary>The facts rules can use, per scope: what a skill author writes in <c>when:</c>.</summary>
public static class FactsPage
{
    public static UIElement Render()
    {
        var search = new AutoSuggestBox { PlaceholderText = "Filter facts", QueryIcon = new SymbolIcon(Symbol.Find), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var list = Ui.Stack(6);
        void Fill()
        {
            list.Children.Clear();
            var filter = search.Text.Trim();
            foreach (var scope in Enum.GetValues<Rules.RuleScope>())
            {
                var facts = Rules.FactCatalog.For(scope)
                    .Where(f => filter.Length == 0 || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || f.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (facts.Count == 0)
                {
                    continue;
                }

                list.Children.Add(Ui.Heading($"{scope.ToString().ToLowerInvariant()} ({facts.Count})"));
                var body = Ui.Stack(2);
                foreach (var fact in facts)
                {
                    body.Children.Add(Ui.Row(new TextBlock { Text = fact.Name, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), MinWidth = 280, IsTextSelectionEnabled = true }, Ui.Muted(fact.Description)));
                }

                list.Children.Add(Ui.Card(body));
            }
        }

        search.TextChanged += (_, _) => Fill();
        Fill();
        return Ui.Page(
            Ui.Header(Glyphs.Library, "Facts", "What a rule's when: expression can test. A fact is null (\"not assessed\") when the scan could not read its area."),
            Ui.Muted("Example: when: navLinks > $maxLinks · when: not homeSiteSet · when: daysSinceActivity > $days and not isHomeSite"),
            search,
            list);
    }
}
