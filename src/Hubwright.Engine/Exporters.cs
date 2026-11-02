using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hubwright.Blueprints;
using Hubwright.Snapshot;
using Hubwright.Theming;

namespace Hubwright.Engine;

/// <summary>
/// Turns a blueprint into things an admin reviews and runs: a PnP PowerShell provisioning script, a
/// site script (theme, header, footer) for a site design, and the theme script. Hubwright never runs
/// them. Every script starts with -WhatIf-style guidance and only creates; nothing is deleted.
/// </summary>
public static class Exporters
{
    public static string ProvisioningScript(Blueprint blueprint)
    {
        var sb = new StringBuilder();
        void L(string line = "") => sb.AppendLine(line);
        L($"# Hubwright provisioning script for \"{blueprint.Name}\" ({blueprint.Created:yyyy-MM-dd}).");
        L("# Review every line first. It only creates sites, hubs and navigation; it never deletes or changes existing sites.");
        L("# Needs PnP.PowerShell 2.x or later and the SharePoint Administrator role: Install-Module PnP.PowerShell -Scope CurrentUser");
        L("#Requires -Modules PnP.PowerShell");
        L("param(");
        L("    [Parameter(Mandatory)] [string] $ClientId,   # your Entra app registration for PnP");
        L($"    [string] $Tenant = \"{blueprint.TenantPrefix}\",");
        L("    [string] $Owner = (Read-Host 'Owner UPN for new sites')");
        L(")");
        L("$ErrorActionPreference = 'Stop'");
        L("$root = \"https://$Tenant.sharepoint.com\"");
        L("Connect-PnPOnline -Url \"https://$Tenant-admin.sharepoint.com\" -Interactive -ClientId $ClientId");
        L();
        L("function Ensure-Site([string]$Slug, [string]$Title, [string]$Kind, [string]$Description) {");
        L("    $url = \"$root/sites/$Slug\"");
        L("    if (Get-PnPTenantSite -Identity $url -ErrorAction SilentlyContinue) { Write-Host \"exists  $url\"; return $url }");
        L("    if ($Kind -eq 'Communication') {");
        L("        New-PnPSite -Type CommunicationSite -Title $Title -Url $url -Description $Description -Owner $Owner -Wait | Out-Null");
        L("    } else {");
        L("        New-PnPSite -Type TeamSite -Title $Title -Alias $Slug -Description $Description -Owners $Owner -Wait | Out-Null");
        L("    }");
        L("    Write-Host \"created $url\"");
        L("    return $url");
        L("}");
        L();
        L("# Home site");
        L($"$homeUrl = Ensure-Site '{Q(blueprint.Home.Slug)}' '{Q(blueprint.Home.Title)}' 'Communication' '{Q(blueprint.Home.Purpose)}'");
        L("Set-PnPHomeSite -HomeSiteUrl $homeUrl");
        foreach (var hub in blueprint.Hubs)
        {
            L();
            L($"# Hub: {hub.Title}");
            L($"$hub = Ensure-Site '{Q(hub.Slug)}' '{Q(hub.Title)}' 'Communication' '{Q(hub.Purpose)}'");
            L("if (-not (Get-PnPHubSite -Identity $hub -ErrorAction SilentlyContinue)) { Register-PnPHubSite -Site $hub | Out-Null }");
            foreach (var site in hub.Sites)
            {
                L($"$site = Ensure-Site '{Q(site.Slug)}' '{Q(site.Title)}' '{site.Kind}' '{Q(site.Purpose)}'");
                L("Add-PnPHubSiteAssociation -Site $site -HubSite $hub");
            }
        }

        foreach (var site in blueprint.Standalone)
        {
            L();
            L($"# Standalone: {site.Title}{(site.Extranet ? " (extranet, deliberately not in a hub)" : "")}");
            L($"Ensure-Site '{Q(site.Slug)}' '{Q(site.Title)}' '{site.Kind}' '{Q(site.Purpose)}' | Out-Null");
        }

        L();
        L("# Hub navigation (run against each hub site)");
        foreach (var hub in blueprint.Hubs)
        {
            L($"Connect-PnPOnline -Url \"$root/sites/{hub.Slug}\" -Interactive -ClientId $ClientId");
            AppendNav(sb, hub.Navigation, "TopNavigationBar", null, 0);
        }

        L();
        L("# Sharing and lifecycle defaults from the blueprint");
        L($"Connect-PnPOnline -Url \"https://$Tenant-admin.sharepoint.com\" -Interactive -ClientId $ClientId");
        L($"Set-PnPTenant -SharingCapability {SharingCapability(blueprint.Policy.Sharing)} -DefaultSharingLinkType {(blueprint.Policy.DefaultLink == DefaultLinkType.SpecificPeople ? "Direct" : "Internal")}" +
          (blueprint.Policy.Sharing == SharingLevel.Anyone ? $" -RequireAnonymousLinksExpireInDays {blueprint.Policy.AnyoneLinkExpirationDays} -FileAnonymousLinkType View -FolderAnonymousLinkType View" : ""));
        L("# Global navigation lives in the SharePoint app bar: configure it on the home site (Settings > Global navigation).");
        L("# Site lifecycle policies (inactive, ownership, attestation) are created in the SharePoint admin center > Site lifecycle management.");
        return sb.ToString();
    }

    private static void AppendNav(StringBuilder sb, IEnumerable<NavNode> nodes, string location, string? parentVariable, int depth)
    {
        var i = 0;
        foreach (var node in nodes)
        {
            var variable = $"$n{depth}_{i++}";
            var url = node.Url is null ? "http://linkless.header/" : node.Url;
            var parent = parentVariable is null ? $"-Location {location}" : $"-Parent {parentVariable}.Id";
            sb.AppendLine(CultureInfo.InvariantCulture, $"{variable} = Add-PnPNavigationNode {parent} -Title '{Q(node.Title)}' -Url '{Q(url)}'");
            AppendNav(sb, node.Children, location, variable, depth + 1);
        }
    }

    /// <summary>A site script for a site design: apply the theme and set the header layout.</summary>
    public static string SiteScript(Blueprint blueprint)
    {
        var theme = blueprint.Theme();
        var script = new JsonObject
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/sp/site-design-script-actions.schema.json",
            ["actions"] = new JsonArray
            {
                new JsonObject { ["verb"] = "applyTheme", ["themeName"] = theme.Name },
                new JsonObject { ["verb"] = "setSiteBranding", ["navigationLayout"] = "Megamenu", ["headerLayout"] = HeaderLayout(blueprint.HeaderLayout), ["headerBackground"] = "None", ["showFooter"] = true },
            },
            ["version"] = 1,
        };
        return script.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ThemeScript(SharePointTheme theme) => theme.ToPowerShell(hideDefaultThemes: true);

    private static string HeaderLayout(string layout) => layout.ToLowerInvariant() switch
    {
        "minimal" => "Minimal",
        "compact" => "Compact",
        "extended" => "Extended",
        _ => "Standard",
    };

    private static string SharingCapability(SharingLevel level) => level switch
    {
        SharingLevel.Anyone => "ExternalUserAndGuestSharing",
        SharingLevel.NewAndExistingGuests => "ExternalUserSharingOnly",
        SharingLevel.ExistingGuests => "ExistingExternalUserSharingOnly",
        _ => "Disabled",
    };

    private static string Q(string? text) => (text ?? "").Replace("'", "''", StringComparison.Ordinal);
}
