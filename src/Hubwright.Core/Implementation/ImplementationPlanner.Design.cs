using System.Globalization;
using Hubwright.Designs;
using Hubwright.Proposals;

namespace Hubwright.Implementation;

/// <summary>
/// Site design steps: for every design marked Include in build, an automatic step drafts the designed
/// page (PnP PowerShell, not published), and a guided step has the site's owner finish it, tell the
/// site's members, publish it and make it the home page. When the person chose Publish (and confirmed
/// it), that step is automatic instead: it records the current home page, publishes the draft and
/// makes it the home page. Nothing is deleted; the old home page stays.
/// </summary>
public static partial class ImplementationPlanner
{
    public const string PnpPageSectionDoc = "https://pnp.github.io/powershell/cmdlets/Add-PnPPageSection.html";
    public const string PnpPageWebPartDoc = "https://pnp.github.io/powershell/cmdlets/Add-PnPPageWebPart.html";
    public const string PnpSetPageDoc = "https://pnp.github.io/powershell/cmdlets/Set-PnPPage.html";
    public const string PnpSetHomePageDoc = "https://pnp.github.io/powershell/cmdlets/Set-PnPHomePage.html";
    public const string PnpGetHomePageDoc = "https://pnp.github.io/powershell/cmdlets/Get-PnPHomePage.html";
    public const string HomePageDoc ="https://support.microsoft.com/en-us/office/use-a-different-page-for-your-sharepoint-site-home-page-35a5022c-f84a-455d-985e-c691ab5dfa17";

    private static void AddDesigns(ImplementationPlan plan, IntranetProposal proposal, string phase)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var design in proposal.Designs.Where(d => d.Apply && d.Blocked is null))
        {
            var url = design.Url.TrimEnd('/');
            var slug = Slug(url);
            for (var n = 2; !used.Add(slug); n++)
            {
                slug = Slug(url) + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            var page = $"{url}/SitePages/{PnpScripts.DesignPageName}.aspx";
            var builds = plan.Steps.Where(s => string.Equals(s.Target?.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)
                && s.Operation is StepOperation.CreateSite or StepOperation.ProvisionPackage or StepOperation.ApplySiteTemplate).Select(s => s.Id).ToList();
            var sections = design.Sections.Where(s => SiteDesigner.Sections.Contains(s)).ToList();
            plan.Steps.Add(new ImplementationStep
            {
                Id = "design-" + slug,
                Phase = phase,
                Title = $"Draft the designed home page of \"{design.Title}\" ({design.PatternName})",
                Detail = $"A new, unpublished page called {PnpScripts.DesignPageName} with {sections.Count} section(s): {string.Join(", ", sections)}. The current home page and every other page stay as they are; a page with this name that already exists is left alone.",
                Kind = StepKind.Automatic,
                Risk = StepRisk.Adds,
                Operation = StepOperation.AddDesignPage,
                Target = url,
                Parameters = { ["name"] = PnpScripts.DesignPageName, ["sections"] = string.Join(",", sections) },
                DependsOn = builds,
                Source = PnpAddPageDoc,
                GuideUrl = url,
                Instructions =
                [
                    $"On {url}: New → Page → Blank, title {PnpScripts.DesignPageName}.",
                    "Add these sections from top to bottom, each with its web part:",
                    .. sections.Select((s, i) => $"  {i + 1}. {SiteDesigner.Describe(s)}"),
                    "Save as draft. Don't publish it yet.",
                ],
                Hint = "design-page",
            });

            List<string> byHand =
            [
                $"Before you switch: agree a date with the site's owner and tell the members of {design.Title} what will change.",
                $"Open {page} → Edit. Configure each web part:",
                .. sections.Select((s, i) => $"  {i + 1}. {SiteDesigner.Describe(s)}"),
                $"Replace every placeholder that starts \"{SampleMarker}\".",
                design.Role == "hub"
                    ? "Navigation: the hub's links are added by its own Build the navigation step."
                    : design.Navigation.Count > 0 ? $"Navigation (Edit next to the site's navigation), once the pages exist: {string.Join(" · ", design.Navigation)}." : "Navigation: keep the site's current links.",
                $"Header: Settings → Change the look → Header → layout {design.HeaderLayout}.",
                "Publish the page, then Settings → Site contents → Site pages → select " + PnpScripts.DesignPageName + " → … → Make homepage. Only a site owner can.",
                "To go back: make the previous home page (usually Home) the home page again. Nothing was deleted.",
            ];

            if (design.Publish)
            {
                // The person chose Publish and confirmed it: the same step, run by PnP PowerShell.
                plan.Steps.Add(new ImplementationStep
                {
                    Id = "design-publish-" + slug,
                    Phase = phase,
                    Title = $"Publish the designed page of \"{design.Title}\" and make it the home page",
                    Detail = $"Publishes {PnpScripts.DesignPageName} as it is (with its placeholders, unless someone edited them) and makes it the site's home page. The current home page is recorded and kept; nothing is deleted. Everyone who opens {design.Title} sees the new page.",
                    Kind = StepKind.Automatic,
                    Risk = StepRisk.Changes,
                    Operation = StepOperation.PublishDesignPage,
                    Target = url,
                    Parameters = { ["name"] = PnpScripts.DesignPageName },
                    DependsOn = ["design-" + slug],
                    Source = PnpSetHomePageDoc,
                    GuideUrl = page,
                    Instructions = byHand,
                    Hint = "publish-design",
                });
                continue;
            }

            plan.Steps.Add(Guide("design-publish-" + slug, phase, $"Owner of \"{design.Title}\": finish, publish and switch to the new home page", page,
                byHand, StepRisk.Changes, [], dependsOn: ["design-" + slug]).With(s =>
                {
                    s.Target = url;
                    s.Source = HomePageDoc;
                    s.Hint = "publish-design";
                }));
        }
    }

    private static string Slug(string url)
    {
        var last = url[(url.LastIndexOf('/') + 1)..].ToLowerInvariant();
        var clean = new string([.. last.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (clean.Contains("--", StringComparison.Ordinal))
        {
            clean = clean.Replace("--", "-", StringComparison.Ordinal);
        }

        return clean.Length == 0 ? "root" : clean;
    }
}
