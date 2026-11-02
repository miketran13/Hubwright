using System.Text.Json;
using Hubwright.Blueprints;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Templates;

namespace Hubwright.Implementation;

/// <summary>Reusable steps: site logo, header and the Microsoft 365 header; look book package values.</summary>
public static partial class ImplementationPlanner
{
    /// <summary>The site logo on one new site (PnP PowerShell): uploaded to Site Assets and set in the header.</summary>
    public static ImplementationStep LogoStepFor(Blueprint blueprint, string siteStep, string title, string url, string phase)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var ext = blueprint.Logo is { } logo ? Path.GetExtension(logo) : ".png";
        return new ImplementationStep
        {
            Id = siteStep + "-logo",
            Phase = phase,
            Title = $"Add the logo to \"{title}\"",
            Detail = blueprint.Logo is null ? "No logo in the proposal yet: add one in Theme and logo, and this step uploads it." : $"Uploads {blueprint.Logo} to the site's Site Assets and sets it as the site logo{(blueprint.LogoIsSquare ? " and thumbnail" : "")}.",
            Kind = StepKind.Automatic,
            Risk = StepRisk.Adds,
            Operation = StepOperation.SetSiteLogo,
            Target = url,
            Parameters = { ["logo"] = blueprint.Logo ?? "", ["fileName"] = "hubwright-logo" + ext, ["square"] = blueprint.LogoIsSquare ? "true" : "false" },
            DependsOn = [siteStep],
            Source = PnpWebHeaderDoc,
            GuideUrl = url,
            Instructions = [$"On {url}: Settings (gear) → Change the look → Header → Site logo → Change → upload the logo{(blueprint.LogoIsSquare ? "; also Site logo thumbnail" : "")} → Save."],
            Hint = "site-logo",
        };
    }

    /// <summary>The Microsoft 365 header (suite bar): the organization theme, set in the Microsoft 365 admin center.</summary>
    public static ImplementationStep SuiteBarStep(Blueprint blueprint, string phase)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var palette = blueprint.Theme().Palette;
        var bar = Theming.Rgb.Parse(palette.GetValueOrDefault("themePrimary", blueprint.BrandColor));
        var text = bar.ContrastWith(Theming.Rgb.White) >= 4.5 ? "#ffffff" : "#000000";
        var step = Guide("m365-header", phase, "Colour the Microsoft 365 header (top bar) to match", "https://admin.microsoft.com/",
            [
                "This bar above every SharePoint page is the Microsoft 365 organization theme, not the SharePoint theme. A Global Administrator sets it; Microsoft documents no API for it.",
                "Microsoft 365 admin center → Settings → Org settings → Organization profile → Custom themes → Default theme.",
                $"Colors → Navigation bar color: {bar.Hex} · Text and icon color: {text} · Accent color: {bar.Hex} → Save.",
                "Logos → Default logo: an HTTPS image URL anyone can open (or an upload under 10 KB); JPG, PNG and GIF are scaled to fit 200 x 48 pixels.",
            ], StepRisk.Changes, []);
        step.Source = OrgThemeDoc;
        return step;
    }

    private static string Layout(string layout) => layout.ToLowerInvariant() switch
    {
        "compact" => "Compact",
        "minimal" => "Minimal",
        "extended" => "Extended",
        _ => "Standard",
    };

    /// <summary>The download link for a look book package on GitHub.</summary>
    public static string RawPackage(string package)
        => package.Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase).Replace("/blob/", "/", StringComparison.Ordinal);

    private static string PackageValue(string parameter, BlueprintSite site) => parameter switch
    {
        "SiteTitle" => site.Title,
        "SiteUrl" => "/sites/" + site.Slug,
        "SiteAlias" => site.Slug,
        _ when parameter.EndsWith("SiteTitle", StringComparison.Ordinal) => $"{site.Title} {parameter[..^"SiteTitle".Length]}",
        _ when parameter.EndsWith("SiteUrl", StringComparison.Ordinal) => $"/sites/{site.Slug}-{parameter[..^"SiteUrl".Length].ToLowerInvariant()}",
        _ => site.Title,
    };

    /// <summary>The site logo, uploaded by hand: SharePoint documents no API Hubwright may call for it.</summary>
    public static ImplementationStep LogoStep(Blueprint blueprint, IEnumerable<string> siteUrls)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var urls = siteUrls.ToList();
        var step = Guide("logo", "2 · Build the structure", blueprint.Logo is null ? "Add your logo to the new sites" : $"Upload the logo to {urls.Count} new site(s)", urls.FirstOrDefault() ?? "",
            [
                blueprint.Logo is null ? "No logo yet: add one in the theme editor (Implement → Edit theme and logo), then come back." : $"The logo file: {blueprint.Logo} (in the proposal folder; Open folder from the Proposal page).",
                "On each site: Settings (gear) → Change the look → Header → Site logo → Change, and Site logo thumbnail (a square version).",
                "Microsoft: pick a logo that fits within 64 x 64 to 192 x 64 pixels (width x height); the thumbnail must be square.",
                .. urls.Select(u => "Site: " + u),
            ], StepRisk.Adds, []);
        step.Hint = "site-logo";
        return step;
    }
}
