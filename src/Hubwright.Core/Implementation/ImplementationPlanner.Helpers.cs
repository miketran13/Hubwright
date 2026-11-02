using System.Text.Json;
using Hubwright.Blueprints;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Templates;

namespace Hubwright.Implementation;

/// <summary>Hints for steps, placeholders between steps, by-hand fallbacks and small builders.</summary>
public static partial class ImplementationPlanner
{
    private static void AssignHints(ImplementationPlan plan, Blueprint blueprint)
    {
        _ = blueprint;
        foreach (var step in plan.Steps)
        {
            step.Hint ??= step.Operation switch
            {
                StepOperation.SetSharing => "sharing-level",
                StepOperation.AddOrUpdateTheme => "theme",
                StepOperation.ApplyTheme => "apply-theme",
                StepOperation.CreateSite => "create-site",
                StepOperation.RegisterHub => "register-hub",
                StepOperation.RenameHub => "rename-hub",
                StepOperation.AssociateWithHub => "associate-with-hub",
                StepOperation.DisassociateFromHub => "keep-extranet-out",
                StepOperation.SetHomeSite => "home-site",
                StepOperation.BuildNavigation => "hub-navigation",
                StepOperation.ApplySiteTemplate => "apply-template",
                StepOperation.ProvisionPackage => "lookbook-package",
                StepOperation.SetSiteHeader => "site-header",
                StepOperation.SetSiteLogo => "site-logo",
                _ => step.Id switch
                {
                    "sharing-links" => "default-links",
                    "permissions" => "review-permissions",
                    "owners" => "add-owners",
                    "unread" => "unread-site",
                    "home-site" => "home-site",
                    "modernize" => "modernize",
                    "subsites" => "flatten-subsites",
                    "archive" => "archive-site",
                    "retire" => "retire-site",
                    "policies" => "lifecycle-policies",
                    "labels" => "sensitivity-labels",
                    "rescan" => "rescan",
                    "global-nav" => "home-site",
                    "m365-header" => "m365-header",
                    var id when id.StartsWith("nav-", StringComparison.Ordinal) => "hub-navigation",
                    var id when id.StartsWith("template-", StringComparison.Ordinal) => "apply-template",
                    var id when id.StartsWith("unhub-", StringComparison.Ordinal) => "unregister-hub",
                    _ when step.Kind == StepKind.Guided && step.Title.StartsWith("Provision", StringComparison.Ordinal) => "lookbook-package",
                    _ => null,
                },
            };
        }
    }

    /// <summary>The automatic steps' outputs referenced by a value like <c>{register-hr-hub.hubSiteId}</c>.</summary>
    public static IEnumerable<string> HubStep(string reference)
        => Placeholder().Match(reference) is { Success: true } m ? [m.Groups["step"].Value] : [];

    /// <summary>
    /// Replaces a placeholder <c>{step-id.output}</c> with that step's output; null when it isn't
    /// there yet. Anything else, including JSON such as a theme, is returned as it is.
    /// </summary>
    public static string? Resolve(ImplementationPlan plan, string value)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(value);
        var match = Placeholder().Match(value);
        return match.Success
            ? plan.Find(match.Groups["step"].Value)?.Outputs.GetValueOrDefault(match.Groups["output"].Value)
            : value;
    }

    /// <summary>Whether a parameter is a placeholder for an earlier step's output.</summary>
    public static bool IsPlaceholder(string value) => Placeholder().IsMatch(value);

    // Exactly {step-id.output}: step ids are lower-case words and hyphens, outputs a single name.
    [System.Text.RegularExpressions.GeneratedRegex(@"^\{(?<step>[a-z0-9]+(-[a-z0-9]+)*)\.(?<output>[A-Za-z][A-Za-z0-9]*)\}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex Placeholder();

    /// <summary>
    /// How to do an automatic step by hand, for when SharePoint refuses it (for example, the account
    /// is a SharePoint admin but not an owner of that site): the page to open and what to do there.
    /// </summary>
    public static (string Url, IReadOnlyList<string> Instructions)? Fallback(ImplementationPlan plan, ImplementationStep step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        var admin = plan.RootUrl.Replace(".sharepoint.com", "-admin.sharepoint.com", StringComparison.OrdinalIgnoreCase);
        var activeSites = admin + "/_layouts/15/online/AdminHome.aspx#/siteManagement";
        var site = step.Target ?? plan.RootUrl;
        string[] owner = [$"Or make yourself a site admin of {site} (Active sites → select it → Membership → Site admins), then run the step again."];
        if (PnpScripts.IsPnp(step.Operation) && step.Instructions.Count > 0)
        {
            return (step.GuideUrl ?? site, step.Instructions);
        }

        return step.Operation switch
        {
            StepOperation.AssociateWithHub => (activeSites, [$"SharePoint admin center → Active sites → select {site} → Hub → associate it with the hub: {step.Title.Replace("Associate ", "", StringComparison.Ordinal)}.", "SharePoint admins can change hub associations here without being a member of the site.", .. owner]),
            StepOperation.DisassociateFromHub => (activeSites, [$"SharePoint admin center → Active sites → select {site} → Hub → disassociate it from its hub.", .. owner]),
            StepOperation.RegisterHub => (activeSites, [$"SharePoint admin center → Active sites → select {site} → Hub → Register as hub site{(step.Parameters.TryGetValue("title", out var t) ? $", named \"{t}\"" : "")}.", .. owner]),
            StepOperation.RenameHub => (activeSites, [$"SharePoint admin center → Active sites → select {site} → Hub → Edit hub site settings → name: {step.Parameters.GetValueOrDefault("title")}.", .. owner]),
            StepOperation.ApplyTheme => (site, [$"On {site}: Settings (gear) → Change the look → Theme → {step.Parameters.GetValueOrDefault("name")} → Save. (Needs owner rights on the site.)"]),
            StepOperation.CreateSite => (activeSites, [$"SharePoint admin center → Active sites → Create → {(step.Parameters.GetValueOrDefault("webTemplate") == "STS#3" ? "Team site (no Microsoft 365 group)" : "Communication site")}: title \"{step.Parameters.GetValueOrDefault("title")}\", address {step.Parameters.GetValueOrDefault("url")}."]),
            StepOperation.AddOrUpdateTheme => (admin, ["Adding a tenant theme needs the SharePoint Administrator role: sign in with an account that has it, then run again (Add-SPOTheme in theme.ps1 does the same)."]),
            StepOperation.SetSharing => (admin + "/_layouts/15/online/AdminHome.aspx#/sharing", [$"SharePoint admin center → Policies → Sharing → set the slider to the planned level ({step.Parameters.GetValueOrDefault("sharingCapability")})."]),
            StepOperation.SetTenantSetting => (admin + "/_layouts/15/online/AdminHome.aspx#/sharing", [$"SharePoint admin center → Policies (Sharing or Access control): set {step.Title}.", $"Or with Microsoft Graph PowerShell: Update-MgAdminSharepointSetting with {step.Parameters.GetValueOrDefault("property")} = {step.Parameters.GetValueOrDefault("value")}."]),
            _ => null,
        };
    }

    public static string SharingCapability(SharingLevel level) => level switch
    {
        SharingLevel.Anyone => "externalUserAndGuestSharing",
        SharingLevel.NewAndExistingGuests => "externalUserSharingOnly",
        SharingLevel.ExistingGuests => "existingExternalUserSharingOnly",
        _ => "disabled",
    };

    private static string? TemplateFor(IntranetProposal proposal, string title) => proposal.NewSites.FirstOrDefault(n => n.Title == title)?.TemplateId;


    private static ImplementationStep Guide(string id, string phase, string title, string url, IEnumerable<string> instructions, StepRisk risk, string[] rules, List<string>? dependsOn = null) => new()
    {
        Id = id,
        Phase = phase,
        Title = title,
        Kind = StepKind.Guided,
        Risk = risk,
        GuideUrl = url,
        Instructions = [.. instructions],
        Rules = [.. rules],
        DependsOn = dependsOn ?? [],
    };

    private static ImplementationStep With(this ImplementationStep step, Action<ImplementationStep> change)
    {
        change(step);
        return step;
    }

    private static IEnumerable<string> Flatten(IEnumerable<NavNode> nodes, int depth = 0)
    {
        foreach (var node in nodes)
        {
            yield return $"{new string(' ', depth * 2)}{node.Title}{(node.Url is null ? " (label)" : ": " + node.Url)}{(node.AudienceTargeted ? " · audience: members" : "")}";
            foreach (var child in Flatten(node.Children, depth + 1))
            {
                yield return child;
            }
        }
    }

    private static string Lcid(string? language) => language?.ToLowerInvariant() switch
    {
        "de-de" => "1031",
        "fr-fr" => "1036",
        "es-es" => "3082",
        "nl-nl" => "1043",
        "it-it" => "1040",
        "sv-se" => "1053",
        "da-dk" => "1030",
        "nb-no" => "1044",
        "fi-fi" => "1035",
        "pt-br" => "1046",
        "ja-jp" => "1041",
        _ => "1033",
    };

    /// <summary>The legacy theme format the documented AddTenantTheme sample uses: isInverted plus palette.</summary>
    public static string ThemeJson(Blueprint b)
    {
        var theme = b.Theme();
        return JsonSerializer.Serialize(new { isInverted = theme.IsInverted, palette = theme.Palette });
    }
}
