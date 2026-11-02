using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Agents;
using Hubwright.Designs;
using Hubwright.Engine;
using Hubwright.Proposals;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Site designs: every site's purpose, pattern, home page and navigation with the reasons and the
/// practices behind them, a preview with sample content, another pattern, Include in build, and a
/// redesign by the architect that waits for the person to accept it. Saves to the intranet proposal.
/// </summary>
public sealed class SiteDesignsPage : Grid
{
    private readonly HubwrightSession _session = App.Current.Session;
    private readonly WebView2 _web = new();
    private readonly ComboBox _sites = new() { Header = "Site", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _detail = Ui.Stack(10);
    private string? _folder;
    private IntranetProposal? _proposal;
    private bool _webReady;
    private string? _pendingHtml;

    public SiteDesignsPage(string? site = null)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(460) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var hint = _session.Skills.HintBook().Find("site-design");
        var panel = Ui.Stack(12,
            Ui.Header(Glyphs.Page, "Site designs", "A design for each site from its purpose. Previews are Hubwright's wireframes with sample content."));
        if (hint is not null)
        {
            panel.Children.Add(Ui.Muted(hint.Why));
        }

        var latest = SiteDesignProposals.Latest(_session.Store);
        if (latest is null)
        {
            panel.Children.Add(Ui.InfoBar("No intranet plan yet", "Make an intranet plan first: every site in it gets a design.", InfoBarSeverity.Informational));
            panel.Children.Add(Ui.Button("Go to Intranet plan", () => App.Current.Window.Show("proposal"), accent: true));
        }
        else if (latest.Value.Proposal.Designs.Count == 0)
        {
            panel.Children.Add(Ui.InfoBar("This plan has no site designs", "It was made before site designs existed. Make a new intranet plan to get them.", InfoBarSeverity.Informational));
            panel.Children.Add(Ui.Button("Go to Intranet plan", () => App.Current.Window.Show("proposal"), accent: true));
        }
        else
        {
            (_folder, _proposal) = latest.Value;
            foreach (var d in _proposal.Designs)
            {
                _sites.Items.Add(new ComboBoxItem { Content = $"{(d.Role == "home" ? "🏠" : d.Role == "hub" ? "🧭" : d.IsNew ? "✨" : "📄")} {d.Title}{(d.Confident ? "" : "  (confirm purpose)")}{(d.Apply ? "  · in build" : "")}", Tag = d.Url });
            }

            panel.Children.Add(Ui.Muted($"{_proposal.Designs.Count} sites · {_proposal.Designs.Count(d => d.Apply)} in the build · {_proposal.Designs.Count(d => !d.Confident)} to confirm · {_proposal.Designs.Count(d => d.Blocked is not null)} blocked"));
            panel.Children.Add(_sites);
            panel.Children.Add(_detail);
            _sites.SelectionChanged += (_, _) => Show();
            var start = site is null ? 0 : Math.Max(0, _proposal.Designs.FindIndex(d => SiteDesigner.Key(d.Url) == SiteDesigner.Key(site)));
            _sites.SelectedIndex = start;
        }

        Children.Add(new ScrollViewer { Content = panel, Padding = new Thickness(24, 16, 16, 24) });
        SetColumn(_web, 1);
        _web.Margin = new Thickness(0, 12, 12, 12);
        Children.Add(_web);
        Loaded += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            _webReady = true;
            if (_pendingHtml is not null)
            {
                _web.NavigateToString(_pendingHtml);
            }
        };
    }

    private SiteDesign? Current => _proposal is not null && (_sites.SelectedItem as ComboBoxItem)?.Tag is string url ? SiteDesigner.Find(_proposal, url) : null;

    private void Preview(string html)
    {
        _pendingHtml = html;
        if (_webReady)
        {
            _web.NavigateToString(html);
        }
    }

    private void Show()
    {
        _detail.Children.Clear();
        if (Current is not { } d || _proposal is null || _folder is null)
        {
            return;
        }

        Preview(SiteDesigns.Preview(_session, _proposal, d.Url));
        _detail.Children.Add(Ui.Row(
            Ui.Badge(d.Role, Ui.Brand),
            Ui.Badge(d.IsNew ? "new site" : "existing site", Ui.Neutral),
            Ui.Badge(d.Confident ? "purpose matched" : "confirm the purpose", d.Confident ? Ui.Good : Ui.Watch),
            Ui.Badge($"by {d.Origin.ToString().ToLowerInvariant()}", Ui.Neutral)));
        _detail.Children.Add(Ui.Strong(d.PatternName));
        _detail.Children.Add(Ui.Text(d.Purpose, 13));
        if (d.Blocked is not null)
        {
            _detail.Children.Add(Ui.InfoBar("Not in the build yet", d.Blocked, InfoBarSeverity.Warning));
        }

        RealSite(d);

        void List(string heading, IEnumerable<string> lines)
        {
            _detail.Children.Add(Ui.Heading(heading));
            foreach (var line in lines)
            {
                _detail.Children.Add(Ui.Text("•  " + line, 13));
            }
        }

        List("Why this design", d.Reasons);
        List("Home page, top to bottom", d.Sections.Select(SiteDesigner.Describe));
        List("Navigation", d.Navigation.Count > 0 ? d.Navigation : [d.Role == "hub" ? "The hub's navigation from the plan (its own Build step)." : d.Role == "home" ? "The global navigation in the app bar (from the plan)." : "The site's current links."]);
        _detail.Children.Add(Ui.Muted($"Header: {d.HeaderLayout} · Template: {d.TemplateName ?? "none"}{(d.IsNew ? "" : " (for reference: existing sites never get a template applied)")} · {d.Audience}"));
        List("Practices it follows", d.Practices);

        var pattern = Ui.Choice("Use another pattern", _session.Skills.Patterns.Where(p => Templates.TemplateMatcher.SameFamily(p.Kind, d.Kind)).Select(p => p.Id), d.Pattern);
        _detail.Children.Add(Ui.Row(pattern, Ui.Button("Apply pattern", () => Change(() => SiteDesigns.UsePattern(_session, _folder, _proposal, d.Url, (string)pattern.SelectedItem)), glyph: Glyphs.Check)).BottomAligned());

        var include = new CheckBox { Content = "Include in build: draft this page on the site (never published; the owner publishes it)", IsChecked = d.Apply, IsEnabled = d.Blocked is null };
        include.Click += (_, _) => Change(() => SiteDesigns.Include(_session, _folder, _proposal, d.Url, include.IsChecked == true));
        _detail.Children.Add(include);
        if (_session.Skills.HintBook().Find("design-page") is { } page && _session.Skills.HintBook().Find("publish-design") is { } publish)
        {
            _detail.Children.Add(Ui.Muted($"In a Build run: {page.What} Then: {publish.What} {publish.Why}"));
        }

        _detail.Children.Add(Ui.Heading("Ask AI to redesign it"));
        var request = Ui.Area("What should change?", "", 90).With(a => a.PlaceholderText = "More self-service: lead with forms and the FAQ; fewer news posts.");
        var answer = Ui.Muted("The architect proposes a change; it waits below and in AI suggestions until you accept it.");
        var ask = Ui.Button("Ask the architect", async () =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return;
            }

            answer.Text = "Thinking…";
            try
            {
                answer.Text = await SiteDesigns.AskAsync(_session, d.Url, request.Text);
                Pending(d);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            {
                answer.Text = "The AI could not answer: " + ex.Message + " (Settings → AI picks the backend.)";
            }
        }, accent: true, glyph: Glyphs.Sparkle);
        _detail.Children.Add(request);
        _detail.Children.Add(ask);
        _detail.Children.Add(answer);
        _pending = Ui.Stack(8);
        _detail.Children.Add(_pending);
        Pending(d);
    }

    private StackPanel _pending = Ui.Stack(8);

    private bool Sample => _session.Workspace.Snapshot?.Source is Snapshot.SnapshotSource.Sample or Snapshot.SnapshotSource.Blueprint;

    /// <summary>The real site: open it or its draft, see where the design stands, apply the draft or publish it.</summary>
    private void RealSite(SiteDesign d)
    {
        var progress = SiteDesigns.Progress(_folder!, d);
        var drafted = progress?.StartsWith("Draft on the site", StringComparison.Ordinal) == true || progress?.StartsWith("Published", StringComparison.Ordinal) == true;
        var exists = !d.IsNew || drafted;
        var card = Ui.Stack(8,
            Ui.Row(Ui.Icon("globe", 22), Ui.Strong("On the real site")),
            Ui.Muted(d.Url),
            Ui.Row(
                Ui.Button("Open site", () => Ui.Open(d.Url), glyph: Glyphs.Open).With(b => b.IsEnabled = exists),
                Ui.Button("Open draft page", () => Ui.Open(SiteDesigns.DraftUrl(d)), glyph: Glyphs.Page).With(b => b.IsEnabled = drafted)));
        if (!exists)
        {
            card.Children.Add(Ui.Muted("A new site: it exists once its Build step created it."));
        }

        card.Children.Add(Ui.Text(progress ?? "Not applied yet: there is no build plan for this proposal yet (applying makes one).", 13));
        if (Sample)
        {
            card.Children.Add(Ui.Muted("Sample or design-only data: there is no real site to apply this to. Scan your tenant and plan from that scan."));
        }
        else if (d.Blocked is null)
        {
            card.Children.Add(Ui.Row(
                Ui.Button("Apply the draft…", () => ApplyAsync(d, publish: false), glyph: Glyphs.Upload),
                Ui.Button("Publish and make home page…", () => ApplyAsync(d, publish: true), accent: true, glyph: Glyphs.Rocket)));
            card.Children.Add(Ui.Muted("Apply adds an unpublished Proposed-home page that only editors see. Publish also makes it the site's home page, so everyone who opens the site sees it. Both ask you to confirm first; nothing is deleted."));
        }

        _detail.Children.Add(Ui.Card(card, Ui.Info));
    }

    /// <summary>Confirms with the person, then runs the site's draft (and publish) steps through the build runner.</summary>
    private async Task ApplyAsync(SiteDesign d, bool publish)
    {
        var (wasApplied, wasPublished) = (d.Apply, d.Publish);
        var (plan, ids) = SiteDesigns.Prepare(_session, _folder!, _proposal!, d.Url, publish);
        var steps = plan.WithDependencies(ids).Where(s => s.Kind == Implementation.StepKind.Automatic && !s.IsFinished).ToList();
        if (steps.Count == 0)
        {
            await Ui.Message(XamlRoot, "Already done", SiteDesigns.Progress(_folder!, d) ?? "Those steps are finished.");
            return;
        }

        var tenant = new TextBox { PlaceholderText = _session.Config.Tenant.Name ?? "", Header = "Type the tenant name to continue" };
        var agreed = new CheckBox { Content = $"The owner of {d.Title} agrees, and its members know what will change." };
        var body = Ui.Stack(10,
            Ui.Text($"{steps.Count} step(s) will change {d.Url}:"),
            Ui.Text(string.Join("\n", steps.Select(s => $"•  {s.Title} ({s.Risk.ToString().ToLowerInvariant()})")), 13));
        if (publish)
        {
            body.Children.Add(Ui.InfoBar("Everyone who opens the site sees this",
                $"The drafted page becomes the home page of {d.Title}, as it is: placeholders and web parts nobody configured stay visible unless someone edited the draft first (Open draft page). The current home page is recorded and kept, so you can switch back; nothing is deleted.",
                InfoBarSeverity.Warning));
            body.Children.Add(agreed);
        }
        else
        {
            body.Children.Add(Ui.Muted("A draft only: nobody but editors sees it until it is published, and the home page stays as it is. A page called Proposed-home that already exists is left alone."));
        }

        body.Children.Add(Ui.Muted("You sign in with the app's write permissions, and your account needs to be an owner of the site. Each step checks first and skips what is already in place. Every change goes to the Audit log."));
        body.Children.Add(tenant);
        var dialog = new ContentDialog
        {
            RequestedTheme = Ui.Theme,
            Title = publish ? $"Publish the new home page of {d.Title}?" : $"Draft the designed page on {d.Title}?",
            Content = new ScrollViewer { Content = body, MaxHeight = 520 },
            PrimaryButtonText = publish ? "Publish" : "Apply the draft",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 720.0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || !string.Equals(tenant.Text.Trim(), _session.Config.Tenant.Name, StringComparison.OrdinalIgnoreCase)
            || (publish && agreed.IsChecked != true))
        {
            SiteDesigns.Restore(_session, _folder!, _proposal!, d.Url, wasApplied, wasPublished);
            await Ui.Message(XamlRoot, "Nothing was changed", publish && agreed.IsChecked != true ? "Publishing needs the owner's agreement: tick the box once the members know." : "Cancelled, or the tenant name didn't match.");
            App.Current.Window.Show("designs", d.Url);
            return;
        }

        var runner = Implementations.Runner(_session, plan, _folder!, () => App.Current.Window.Handle);
        var report = new Progress<StepEvent>(e => Activity.Report($"{e.Step.Title}: {e.Message}"));
        await Task.Run(() => runner.RunAsync(ids, report, CancellationToken.None));
        var failed = steps.Select(s => plan.Find(s.Id)).FirstOrDefault(s => s?.Status is Implementation.StepStatus.Failed or Implementation.StepStatus.Blocked);
        await Ui.Message(XamlRoot, failed is null ? (publish ? "Published" : "Draft added") : "Not finished",
            failed is null ? SiteDesigns.Progress(_folder!, d) ?? "Done." : $"{failed.Title}: {failed.Result}\n\nThe by-hand steps are in Build, on this step.");
        App.Current.Window.Show("designs", d.Url);
    }

    /// <summary>Redesigns waiting for this site: preview, accept or reject.</summary>
    private void Pending(SiteDesign d)
    {
        _pending.Children.Clear();
        var waiting = _session.Proposals.All().Where(p => p.Kind == Storage.ProposalKind.SiteDesign && p.Status == ProposalStatus.Pending
            && SiteDesigner.Key(SiteDesignProposals.Read(p).Url) == SiteDesigner.Key(d.Url)).ToList();
        if (waiting.Count == 0)
        {
            return;
        }

        _pending.Children.Add(Ui.Heading($"Waiting for you ({waiting.Count})"));
        foreach (var p in waiting)
        {
            var proposed = SiteDesignProposals.Read(p);
            _pending.Children.Add(Ui.Card(Ui.Stack(6,
                Ui.Strong(p.Title),
                Ui.Text(p.Summary, 13),
                Ui.Muted($"Sections: {string.Join(", ", proposed.Sections)} · Navigation: {string.Join(" · ", proposed.Navigation)} · by {p.ProposedBy}"),
                Ui.Row(
                    Ui.Button("Preview", () => Preview(SiteDesigns.Preview(_session, _proposal!, instead: proposed)), glyph: Glyphs.Open),
                    Ui.Button("Back to current", () => Preview(SiteDesigns.Preview(_session, _proposal!, d.Url))),
                    Ui.Button("Accept", () => Change(() => ProposalsPage.Decide(p, accept: true, reload: false)), accent: true, glyph: Glyphs.Check),
                    Ui.Button("Reject", () => Change(() => ProposalsPage.Decide(p, accept: false, reload: false)), glyph: Glyphs.Cancel))), Ui.Watch));
        }
    }

    private void Change(Action change)
    {
        var url = Current?.Url;
        try
        {
            change();
        }
        catch (InvalidOperationException ex)
        {
            _detail.Children.Insert(0, Ui.InfoBar("Not changed", ex.Message, InfoBarSeverity.Error));
            return;
        }

        App.Current.Window.Show("designs", url);
    }

    private void Change(Func<SiteDesign> change) => Change(() => { _ = change(); });
}
