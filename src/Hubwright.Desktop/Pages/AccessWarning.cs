using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Snapshot;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// The sites the scan could not fully read with your access (can't open, or can't read their
/// permissions), as a warning on Score, Findings, Structure and Reports: how many, which ones, and how
/// to see or get access in the admin center. Nothing is shown when the scan read every site. Hubwright
/// never grants access itself; the person decides.
/// </summary>
public static class AccessWarning
{
    /// <summary>The warning for the open tenant's snapshot, or null when every site was read.</summary>
    public static UIElement? For(TenantSnapshot? snapshot)
    {
        if (snapshot is null || SiteAccess.Unread(snapshot) is not { Count: > 0 } unread)
        {
            return null;
        }

        var list = Ui.Stack(6);
        foreach (var site in unread)
        {
            var url = site.Url;
            list.Children.Add(Ui.Row(
                Ui.Icon("lock_shield", 16),
                Ui.Strong(site.Title),
                Ui.Badge(site.Level == SiteAccessLevel.CannotOpen ? "can't open" : "permissions hidden", site.Level == SiteAccessLevel.CannotOpen ? Ui.Problem : Ui.Watch),
                Ui.Muted($"{SiteAccess.Kind(site.Kind)}{(site.LastActivity is { } last ? $" · active {last:yyyy-MM-dd}" : "")}{(site.StorageUsedMb is { } mb ? $" · {mb:N0} MB" : "")} · {SiteAccess.Missing(site.Level)}"),
                Ui.Link(url, url),
                Ui.Button("Open site", () => Ui.Open(url), glyph: Glyphs.Open),
                Ui.Button("Copy address", () => Copy(url))));
        }

        var steps = Ui.Stack(4, Ui.Strong("To see or get access"));
        var number = 0;
        foreach (var step in SiteAccess.HowToGetAccess)
        {
            steps.Children.Add(Ui.Text($"{++number}.  {step}", 13));
        }

        steps.Children.Add(Ui.Row(
            Ui.Button("Open Active sites (admin center)", () => Ui.Open(SiteAccess.ActiveSitesLink), accent: true, glyph: Glyphs.Shield),
            Ui.Button("Copy the list", () => Copy(string.Join(Environment.NewLine, unread.Select(s => $"{s.Title}\t{s.Url}")))),
            Ui.Button("Save as CSV…", async () => await SaveAs.TextAsync($"{snapshot.Tenant}-sites-not-read", "CSV", ".csv", SiteAccess.Csv(unread)), glyph: Glyphs.Save),
            Ui.Link("Manage site admins (Microsoft Learn)", SiteAccess.ManageSiteAdminsDoc)));

        var bar = Ui.InfoBar(SiteAccess.Summary(unread), SiteAccess.Explanation, InfoBarSeverity.Warning);
        bar.Content = new Expander
        {
            Header = $"Show the {unread.Count} site{(unread.Count == 1 ? "" : "s")} and how to get access",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 12),
            Content = Ui.Stack(12, new ScrollViewer { Content = list, MaxHeight = 320 }, steps),
        };
        return bar;
    }

    /// <summary>Puts the warning right under a page's header (the first child of Ui.Page's stack), when there is one.</summary>
    public static ScrollViewer Insert(ScrollViewer page, TenantSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (For(snapshot) is { } warning && page.Content is StackPanel stack)
        {
            stack.Children.Insert(Math.Min(1, stack.Children.Count), warning);
        }

        return page;
    }

    private static void Copy(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}
