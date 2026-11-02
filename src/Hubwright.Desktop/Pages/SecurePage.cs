using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Engine;
using Hubwright.Journey;

namespace Hubwright.Desktop.Pages;

/// <summary>
/// Stage 5: sharing, permissions and Copilot oversharing in one place, with grades, what couldn't be
/// read, and every finding with its fix and source. Same rules as Findings; nothing here writes.
/// </summary>
public static class SecurePage
{
    public static UIElement Render()
    {
        var session = App.Current.Session;
        var assessment = session.Workspace.Assessment;
        var header = Ui.Header(Glyphs.Shield, "Security", "Sharing, guests, permissions and Copilot oversharing, checked against Microsoft's guidance. The baseline sets the tenant's defaults; findings show what to fix on each site.");
        if (assessment is null)
        {
            return Ui.Page(header, Ui.InfoBar("No scan yet", "Scan your tenant on the Setup page, or load a sample on Home.", InfoBarSeverity.Warning), Ui.Button("Go to Setup", () => App.Current.Window.Show("setup"), accent: true));
        }

        var stage = JourneyStatus.For(session).First(s => s.Id == "secure");
        var watched = assessment.Categories.Where(c => IntranetJourney.SecurityCategories.Contains(c.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        var tiles = watched.Select(c => (UIElement)Ui.Tile(c.Category, c.Grade, Ui.ScoreColor(c.Score), $"{c.Failed} failed · {c.NotAssessed} not assessed")).ToArray();
        var findings = assessment.Findings.Where(f => IntranetJourney.SecurityCategories.Contains(f.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        var list = Ui.Stack(10);
        foreach (var group in FindingsPage.Groups(findings))
        {
            list.Children.Add(group);
        }

        if (findings.Count == 0)
        {
            list.Children.Add(Ui.InfoBar("No security findings", "Nothing failed in sharing, permissions or Copilot readiness.", InfoBarSeverity.Success));
        }

        var unread = watched.Sum(c => c.NotAssessed);
        var baseline = new List<UIElement>();
        foreach (var profile in Enum.GetValues<Security.BaselineProfile>())
        {
            var items = SecurityBaselines.Compare(session, profile);
            baseline.Add(Ui.Card(Ui.Stack(6,
                Ui.Strong(profile.ToString()),
                Ui.Muted($"{items.Count(i => i.State == Security.BaselineState.Matches)} in place · {items.Count(i => i.State == Security.BaselineState.Differs)} to change · {items.Count(i => i.State == Security.BaselineState.NotRead)} not read"),
                Ui.Muted(profile switch
                {
                    Security.BaselineProfile.Standard => "Guests who sign in, organization links by default, expiring guest access, legacy authentication off.",
                    Security.BaselineProfile.Strict => "Existing guests only, specific-people links, idle sign-out, web-only access from unmanaged devices, labels.",
                    _ => "No external sharing, unmanaged devices blocked, labels and DLP required, backup.",
                }))));
        }

        return Ui.Page(
            header,
            Ui.Heading("Security baseline"),
            Ui.Columns(3, [.. baseline]),
            Ui.Button("Open the baseline", () => App.Current.Window.Show("baseline"), accent: true, glyph: Glyphs.Check),
            Ui.Heading("Findings"),
            Ui.InfoBar(stage.State == StageState.Done ? "Secure" : "To review", stage.Status, stage.State == StageState.Done ? InfoBarSeverity.Success : InfoBarSeverity.Warning),
            Ui.Columns(Math.Max(1, tiles.Length), tiles),
            unread > 0 ? Ui.InfoBar("Not assessed is not a pass", $"{unread} security check(s) couldn't be read with your permissions. See the Dashboard's not-assessed list.", InfoBarSeverity.Informational) : new Border(),
            Ui.Heading($"Findings ({findings.Count})"),
            list);
    }
}
