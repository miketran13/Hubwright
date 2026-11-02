using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Engine;
using Hubwright.Monitoring;

namespace Hubwright.Desktop.Pages;

/// <summary>Saves text through the Save dialog; returns the path, or null when cancelled.</summary>
internal static class SaveAs
{
    public static async Task<string?> TextAsync(string suggested, string label, string extension, string content)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = suggested };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.Window.Handle);
        picker.FileTypeChoices.Add(label, [extension]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }

        await File.WriteAllTextAsync(file.Path, content);
        App.Current.Session.Audit.Append("export", file.Path);
        return file.Path;
    }
}

/// <summary>
/// Stage 6: what drifted from the plan and the baseline since they were built, the grade across
/// scans, a short status to share, and scheduled scans. Nothing here writes to the tenant.
/// </summary>
public static class MonitorPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var header = Ui.Header(Glyphs.History, "Drift and trend", "Compares the latest scan with your plan and security baseline, and every scan with the ones before it. Scan again to see today.");
        if (session.Workspace.Snapshot is null)
        {
            return Ui.Page(header, Ui.InfoBar("No scan yet", "Scan the tenant on the Setup page, or load a sample on Home.", InfoBarSeverity.Warning));
        }

        var drift = IntranetMonitor.Drift(session);
        var driftList = Ui.Stack(6);
        foreach (var group in drift.GroupBy(d => d.Area))
        {
            driftList.Children.Add(Ui.Strong(group.Key));
            foreach (var d in group)
            {
                var (label, color) = d.State switch
                {
                    DriftState.InPlace => ("in place", Ui.Good),
                    DriftState.Drifted => ("drifted", Ui.Problem),
                    DriftState.NotBuilt => ("to build", Ui.Neutral),
                    _ => ("not read", Ui.Watch),
                };
                driftList.Children.Add(Ui.Row(Ui.Badge(label, color), Ui.Text(d.Title, 13).With(t => t.MinWidth = 320), Ui.Muted($"expected {d.Expected} · now {d.Actual ?? "not read"}")));
            }
        }

        if (drift.Count == 0)
        {
            driftList.Children.Add(Ui.Muted("Nothing to compare yet: make a plan (Plan) or a security baseline (Secure) first."));
        }

        var points = IntranetMonitor.Trend(session);
        var categories = points.SelectMany(p => p.Categories.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        string[] headings = ["Scan", "Overall", "Findings", .. categories];
        for (var c = 0; c < headings.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(grid, Ui.Muted(headings[c]), 0, c);
        }

        for (var r = 0; r < points.Count; r++)
        {
            var p = points[r];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(grid, Ui.Text($"{p.At.ToLocalTime():yyyy-MM-dd HH:mm}", 13), r + 1, 0);
            Place(grid, Ui.Badge(Ui.Score(p.Overall), Ui.ScoreColor(p.Overall)), r + 1, 1);
            Place(grid, Ui.Text(p.Findings.ToString(System.Globalization.CultureInfo.InvariantCulture), 13), r + 1, 2);
            for (var c = 0; c < categories.Count; c++)
            {
                var score = p.Categories.GetValueOrDefault(categories[c]);
                Place(grid, new TextBlock { Text = Ui.Score(score), FontSize = 13, Foreground = Ui.Brush(Ui.ScoreColor(score)) }, r + 1, c + 3);
            }
        }

        grid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        var summary = IntranetMonitor.Summary(session);
        var scheduleStatus = Ui.Muted("");
        async Task Schedule(string frequency)
        {
            try
            {
                var cli = App.CliPath ?? throw new InvalidOperationException("hubwright.exe isn't next to the app, so there is nothing to schedule.");
                scheduleStatus.Text = await IntranetMonitor.ScheduleAsync(session.Store.Tenant, frequency, cli);
                session.Audit.Append("schedule", $"{session.Store.Tenant} {frequency}");
            }
            catch (InvalidOperationException ex)
            {
                scheduleStatus.Text = ex.Message;
            }
        }

        return Ui.Page(
            header,
            Ui.Heading($"Drift ({drift.Count(d => d.State == DriftState.Drifted)} drifted)"),
            Ui.Card(Ui.Stack(8, driftList, Ui.Button("Export to Excel (CSV)…", async () => await SaveAs.TextAsync($"{session.Store.Tenant}-drift", "CSV", ".csv", CsvExport.Drift(drift)), glyph: Glyphs.Save))),
            Ui.Heading("Grade across scans"),
            Ui.Card(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Auto, Content = grid }),
            Ui.Heading("Share a status"),
            Ui.Card(Ui.Stack(8,
                Ui.Text(summary, 13),
                Ui.Row(
                    Ui.Button("Copy for Teams or email", () =>
                    {
                        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                        data.SetText(summary);
                        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                    }, accent: true, glyph: Glyphs.Chat),
                    Ui.Button("Save as…", async () => await SaveAs.TextAsync($"{session.Store.Tenant}-status", "Markdown", ".md", summary), glyph: Glyphs.Save)))),
            Ui.Heading("Scheduled scans"),
            Ui.Card(Ui.Stack(8,
                Ui.Muted("Read-only scans with Windows Task Scheduler, as you, at 07:00. They never ask you to sign in: when your sign-in can't be renewed silently, the scan stops and the audit log says so."),
                Ui.Row(
                    Ui.Button("Weekly", () => Schedule("weekly"), glyph: Glyphs.History),
                    Ui.Button("Daily", () => Schedule("daily")),
                    Ui.Button("Off", () => Schedule("off"), glyph: Glyphs.Cancel),
                    Ui.Button("Status", () => Schedule("status"), glyph: Glyphs.Refresh)),
                scheduleStatus)));
    }

    private static void Place(Grid grid, FrameworkElement element, int row, int column)
    {
        while (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
}

/// <summary>Sites below the minimum number of owners, as a list to confirm with each site's members.</summary>
public static class OwnersPage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var header = Ui.Header(Glyphs.People, "Owners", "Every site needs accountable owners; two cover holidays and people leaving. Export the list, ask each site's members who owns it, then add them in the admin center.");
        if (session.Workspace.Snapshot is null)
        {
            return Ui.Page(header, Ui.InfoBar("No scan yet", "Scan the tenant on the Setup page, or load a sample on Home.", InfoBarSeverity.Warning));
        }

        var rows = IntranetMonitor.Owners(session);
        var list = Ui.Stack(4);
        foreach (var r in rows.Take(300))
        {
            list.Children.Add(Ui.Row(
                Ui.Badge(r.Status, r.Owners switch { null => Ui.Watch, 0 => Ui.Problem, _ => Ui.Neutral }),
                Ui.Text(r.Title, 13).With(t => t.MinWidth = 260),
                Ui.Muted(r.Hub is null ? "no hub" : r.Hub),
                new HyperlinkButton { Content = "Open ↗", NavigateUri = Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u : null, Padding = new Thickness(4, 0, 4, 0), FontSize = 12 }));
        }

        return Ui.Page(
            header,
            Ui.Row(
                Ui.Button("Export to Excel (CSV)…", async () => await SaveAs.TextAsync($"{session.Store.Tenant}-owners", "CSV", ".csv", CsvExport.Owners(rows)), accent: true, glyph: Glyphs.Save),
                Ui.Muted($"{rows.Count} site(s) below the minimum; the site ownership policy (Secure → Baseline) keeps finding them.")),
            rows.Count == 0 ? Ui.InfoBar("Every site has enough owners", "Nothing to confirm.", InfoBarSeverity.Success) : list);
    }
}
