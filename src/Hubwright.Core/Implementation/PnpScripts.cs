using System.Globalization;
using System.Text;
using System.Text.Json;
using Hubwright.Snapshot;

namespace Hubwright.Implementation;

/// <summary>
/// The PnP PowerShell scripts behind the steps SharePoint documents no REST call for: setting the
/// home site, building navigation, applying Microsoft's built-in templates, provisioning look book
/// packages, the site header and the site logo. Every script is generated here from a fixed set of
/// cmdlets (<see cref="Cmdlets"/>); values are single-quoted with quotes doubled, and the access
/// token is only ever read from an environment variable, never written into the script.
/// </summary>
public static class PnpScripts
{
    /// <summary>The only PnP cmdlets Hubwright runs. None of them deletes anything.</summary>
    public static readonly IReadOnlyList<string> Cmdlets =
    [
        "Connect-PnPOnline", "Set-PnPHomeSite", "Get-PnPNavigationNode", "Add-PnPNavigationNode",
        "Invoke-PnPSiteDesign", "Invoke-PnPTenantTemplate", "Set-PnPWebHeader", "Add-PnPFile",
        "Get-PnPTenant", "Set-PnPTenant", "Get-PnPOrgNewsSite", "Add-PnPOrgNewsSite", "Get-PnPPage", "Add-PnPPage", "Add-PnPPageTextPart",
        "Add-PnPPageSection", "Add-PnPPageWebPart", "Get-PnPHomePage", "Set-PnPPage", "Set-PnPHomePage",
    ];

    /// <summary>
    /// The web part each design section becomes (Add-PnPPageWebPart -DefaultWebPartType, values from
    /// pnp.github.io/powershell/cmdlets/Add-PnPPageWebPart.html); null means a text block with
    /// placeholder text that says what to put there.
    /// </summary>
    public static string? WebPartFor(string section) => section switch
    {
        "hero" => "Hero",
        "news" => "News",
        "events" or "calendar" => "Events",
        "quicklinks" => "QuickLinks",
        "people" => "People",
        "gallery" => "ImageGallery",
        "video" => "VideoEmbed",
        _ => null,
    };

    /// <summary>The page a design is drafted as. Never the site's home page until its owner makes it one.</summary>
    public const string DesignPageName = "Proposed-home";

    /// <summary>
    /// The only Set-PnPTenant parameters Hubwright sets (the security baseline). Each is documented on
    /// pnp.github.io/powershell/cmdlets/Set-PnPTenant.html; PnpShell refuses any other parameter.
    /// </summary>
    public static readonly IReadOnlyList<string> TenantParameters =
    [
        "DefaultSharingLinkType", "DefaultLinkPermission", "RequireAnonymousLinksExpireInDays", "FileAnonymousLinkType",
        "FolderAnonymousLinkType", "ExternalUserExpirationRequired", "ExternalUserExpireInDays",
    ];

    /// <summary>The value a script replaced, so it can be restored.</summary>
    public const string PreviousMarker = "HUBWRIGHT-PREVIOUS:";

    public const string TokenVariable = "HUBWRIGHT_TOKEN";
    public const string LogoVariable = "HUBWRIGHT_LOGO";
    public const string ResultMarker = "HUBWRIGHT-RESULT:";

    /// <summary>Starts the line with the exception message when a script stops, so the reason isn't lost in PowerShell's error layout.</summary>
    public const string ErrorMarker = "HUBWRIGHT-ERROR:";

    public static bool IsPnp(StepOperation operation) => operation is StepOperation.SetHomeSite or StepOperation.BuildNavigation
        or StepOperation.ApplySiteTemplate or StepOperation.ProvisionPackage or StepOperation.SetSiteHeader or StepOperation.SetSiteLogo
        or StepOperation.SetTenantPolicy or StepOperation.AddOrgNewsSite or StepOperation.AddStarterPage or StepOperation.AddDesignPage or StepOperation.PublishDesignPage;

    /// <summary>Whether the step connects to the admin site (Set-PnPHomeSite needs it).</summary>
    public static bool NeedsAdmin(StepOperation operation) => operation is StepOperation.SetHomeSite or StepOperation.SetTenantPolicy or StepOperation.AddOrgNewsSite;

    /// <summary>The URL the script connects to.</summary>
    public static string ConnectUrl(ImplementationPlan plan, ImplementationStep step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        return step.Operation switch
        {
            StepOperation.SetHomeSite or StepOperation.SetTenantPolicy or StepOperation.AddOrgNewsSite => plan.RootUrl.Replace(".sharepoint.com", "-admin.sharepoint.com", StringComparison.OrdinalIgnoreCase),
            StepOperation.ProvisionPackage => plan.RootUrl,
            _ => step.Target ?? plan.RootUrl,
        };
    }

    public static string Script(ImplementationPlan plan, ImplementationStep step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        string P(string key) => step.Parameters.TryGetValue(key, out var v) ? ImplementationPlanner.Resolve(plan, v) ?? v : "";
        var target = step.Target ?? plan.RootUrl;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"trap {{ [Console]::Error.WriteLine({Q(ErrorMarker + " ")} + $_.Exception.Message); exit 1 }}");
        sb.AppendLine("Import-Module PnP.PowerShell");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Connect-PnPOnline -Url {Q(ConnectUrl(plan, step))} -AccessToken $env:{TokenVariable}");
        switch (step.Operation)
        {
            case StepOperation.SetHomeSite:
                sb.AppendLine(CultureInfo.InvariantCulture, $"Set-PnPHomeSite -HomeSiteUrl {Q(target)} -Force");
                Result(sb, $"Home site set to {target}.");
                break;

            case StepOperation.BuildNavigation:
            {
                var nodes = JsonSerializer.Deserialize<List<NavNode>>(P("nav"), TenantSnapshot.Json) ?? [];
                sb.AppendLine("$existing = @(Get-PnPNavigationNode -Location TopNavigationBar)");
                sb.AppendLine("$added = 0");
                sb.AppendLine("function Add-Node($title, $url, $parent) {");
                sb.AppendLine("  $siblings = if ($parent) { @((Get-PnPNavigationNode -Id $parent.Id).Children) } else { $script:existing }");
                sb.AppendLine("  $found = $siblings | Where-Object { $_.Title -eq $title } | Select-Object -First 1");
                sb.AppendLine("  if ($found) { return $found }");
                sb.AppendLine("  $node = @{ Location = 'TopNavigationBar'; Title = $title }");
                sb.AppendLine("  if ($url) { $node.Url = $url; $node.External = $true }");
                sb.AppendLine("  if ($parent) { $node.Parent = $parent.Id }");
                sb.AppendLine("  $script:added++");
                sb.AppendLine("  return Add-PnPNavigationNode @node");
                sb.AppendLine("}");
                var index = 0;
                void Emit(NavNode node, string? parent, int depth)
                {
                    var name = "$n" + (index++).ToString(CultureInfo.InvariantCulture);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{name} = Add-Node {Q(node.Title)} {(node.Url is null ? "$null" : Q(node.Url))} {parent ?? "$null"}");
                    if (depth < 2)
                    {
                        foreach (var child in node.Children)
                        {
                            Emit(child, name, depth + 1);
                        }
                    }
                }

                foreach (var node in nodes.Where(n => !string.Equals(n.Title, "Home", StringComparison.OrdinalIgnoreCase)))
                {
                    Emit(node, null, 0);
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output \"{ResultMarker} $added navigation link(s) added; links already there were kept.\"");
                break;
            }

            case StepOperation.ApplySiteTemplate:
                sb.AppendLine(CultureInfo.InvariantCulture, $"Invoke-PnPSiteDesign -Template {Identifier(P("template"))} -WebUrl {Q(target)} | Out-Null");
                Result(sb, $"Template {P("template")} applied to {target}.");
                break;

            case StepOperation.ProvisionPackage:
            {
                sb.AppendLine("$package = Join-Path ([System.IO.Path]::GetTempPath()) ('hubwright-' + [guid]::NewGuid() + '.pnp')");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Invoke-WebRequest -Uri {Q(P("package"))} -OutFile $package -UseBasicParsing");
                var parameters = string.Join("; ", step.Parameters.Where(kv => kv.Key.StartsWith("p:", StringComparison.Ordinal)).Select(kv => $"{Q(kv.Key[2..])} = {Q(kv.Value)}"));
                sb.AppendLine("try {");
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Invoke-PnPTenantTemplate -Path $package -Parameters @{{ {parameters} }}");
                sb.AppendLine("} finally { Remove-Item $package -ErrorAction SilentlyContinue }");
                Result(sb, $"Package provisioned at {target}.");
                break;
            }

            case StepOperation.SetSiteHeader:
                sb.AppendLine(CultureInfo.InvariantCulture, $"Set-PnPWebHeader -HeaderLayout {Identifier(P("layout"))} -HeaderEmphasis {Identifier(P("emphasis"))}");
                Result(sb, $"Header: {P("layout")} layout, {P("emphasis")} colour.");
                break;

            case StepOperation.SetSiteLogo:
                sb.AppendLine(CultureInfo.InvariantCulture, $"$file = Add-PnPFile -Path $env:{LogoVariable} -Folder 'SiteAssets' -NewFileName {Q(P("fileName"))}");
                sb.AppendLine(P("square") == "true"
                    ? "Set-PnPWebHeader -SiteLogoUrl $file.ServerRelativeUrl -SiteThumbnailUrl $file.ServerRelativeUrl"
                    : "Set-PnPWebHeader -SiteLogoUrl $file.ServerRelativeUrl");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output \"{ResultMarker} Logo uploaded to $($file.ServerRelativeUrl) and set.\"");
                break;

            case StepOperation.AddOrgNewsSite:
                sb.AppendLine(CultureInfo.InvariantCulture, $"if (@(Get-PnPOrgNewsSite) | Where-Object {{ \"$_\".TrimEnd('/') -eq {Q(target.TrimEnd('/'))} }}) {{ Write-Output {Q(ResultMarker + " Already an organization news site.")}; exit 0 }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Add-PnPOrgNewsSite -OrgNewsSiteUrl {Q(target)}");
                Result(sb, $"{target} is now an organization news site.");
                break;

            case StepOperation.AddStarterPage:
            {
                var name = Identifier(P("name"));
                sb.AppendLine(CultureInfo.InvariantCulture, $"try {{ $page = Get-PnPPage -Identity {Q(name)} }} catch {{ $page = $null }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"if ($page) {{ Write-Output {Q(ResultMarker + " Already has a page called " + name + "; left as it is.")}; exit 0 }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Add-PnPPage -Name {Q(name)} -LayoutType Article -PromoteAs NewsArticle | Out-Null");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Add-PnPPageTextPart -Page {Q(name)} -Text {Q(P("text"))} -Section 1 -Column 1 | Out-Null");
                Result(sb, $"Draft news post \"{name}\" added to {target} (not published).");
                break;
            }

            case StepOperation.AddDesignPage:
            {
                var name = P("name");
                _ = Identifier(name.Replace("-", "", StringComparison.Ordinal));
                sb.AppendLine(CultureInfo.InvariantCulture, $"try {{ $page = Get-PnPPage -Identity {Q(name)} }} catch {{ $page = $null }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"if ($page) {{ Write-Output {Q(ResultMarker + " Already has a page called " + name + "; left as it is.")}; exit 0 }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"$page = Add-PnPPage -Name {Q(name)} -LayoutType Home");
                var sections = P("sections").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var order = 0;
                foreach (var section in sections)
                {
                    order++;
                    var template = section == "hero" ? "OneColumnFullWidth" : "OneColumn";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Add-PnPPageSection -Page $page -SectionTemplate {template} -Order {order} | Out-Null");
                    var part = WebPartFor(section);
                    sb.AppendLine(part is null
                        ? string.Create(CultureInfo.InvariantCulture, $"Add-PnPPageTextPart -Page $page -Text {Q($"<p>{ImplementationPlanner.SampleMarker}</p><p>{System.Net.WebUtility.HtmlEncode(Designs.SiteDesigner.Describe(Identifier(section)))}</p>")} -Section {order} -Column 1 | Out-Null")
                        : string.Create(CultureInfo.InvariantCulture, $"Add-PnPPageWebPart -Page $page -DefaultWebPartType {Identifier(part)} -Section {order} -Column 1 | Out-Null"));
                }

                Result(sb, $"Draft page {name} added to {target} with {sections.Length} section(s); not published, and the home page is unchanged.");
                break;
            }

            case StepOperation.PublishDesignPage:
            {
                // Reads first: no draft, no change; already the home page, nothing to do; else the old home page is recorded for going back.
                var name = P("name");
                _ = Identifier(name.Replace("-", "", StringComparison.Ordinal));
                sb.AppendLine(CultureInfo.InvariantCulture, $"try {{ $page = Get-PnPPage -Identity {Q(name)} }} catch {{ $page = $null }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"if (-not $page) {{ throw {Q("There is no " + name + " page on this site to publish: run the draft step first.")} }}");
                sb.AppendLine("$before = \"$(Get-PnPHomePage)\"");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output (\"{PreviousMarker} \" + $before)");
                sb.AppendLine(CultureInfo.InvariantCulture, $"if ($before -like {Q("*" + name + ".aspx")}) {{ Write-Output {Q(ResultMarker + " Already published and the home page.")}; exit 0 }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Set-PnPPage -Identity {Q(name)} -Publish | Out-Null");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Set-PnPHomePage -RootFolderRelativeUrl {Q("SitePages/" + name + ".aspx")}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output ({Q(ResultMarker + " Published " + name + " and made it the home page. The previous home page (")} + $before + {Q(") is kept: make it the home page again to go back.")})");
                break;
            }

            case StepOperation.SetTenantPolicy:
            {
                var parameter = P("parameter");
                if (!TenantParameters.Contains(parameter, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Hubwright does not set {parameter}: it is not one of its baseline parameters.");
                }

                var value = P("value");
                sb.AppendLine(CultureInfo.InvariantCulture, $"$before = \"$((Get-PnPTenant).{parameter})\"");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output (\"{PreviousMarker} \" + $before)");
                // A maximum number of days is met by any shorter, non-zero value: the baseline never loosens a stricter setting.
                var already = P("type") == "int" ? $"([int]$before -gt 0 -and [int]$before -le {Literal(value, "int")})" : $"($before -eq {Q(value)})";
                sb.AppendLine(CultureInfo.InvariantCulture, $"if {already} {{ Write-Output \"{ResultMarker} Already $before.\"; exit 0 }}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Set-PnPTenant -{parameter} {Literal(value, P("type"))}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output \"{ResultMarker} Changed from $before to {value.Replace("\"", "", StringComparison.Ordinal)}.\"");
                break;
            }

            default:
                throw new InvalidOperationException($"{step.Operation} is not a PnP step.");
        }

        return sb.ToString();
    }

    /// <summary>A PowerShell single-quoted string: nothing inside is expanded; quotes are doubled.</summary>
    public static string Q(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal).Replace("‘", "''", StringComparison.Ordinal).Replace("’", "''", StringComparison.Ordinal) + "'";
    }

    /// <summary>An enum value (template, layout): letters and digits only, or the script is refused.</summary>
    public static string Identifier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value.All(char.IsAsciiLetterOrDigit) ? value : throw new InvalidOperationException($"'{value}' is not a valid PnP value.");
    }

    /// <summary>A Set-PnPTenant value: $true/$false, a whole number, or an enum name; anything else is refused.</summary>
    public static string Literal(string value, string type)
    {
        ArgumentNullException.ThrowIfNull(value);
        return type switch
        {
            "bool" => value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "$true" : value.Equals("false", StringComparison.OrdinalIgnoreCase) ? "$false" : throw new InvalidOperationException($"'{value}' is not true or false."),
            "int" => value.Length is > 0 and < 6 && value.All(char.IsAsciiDigit) ? value : throw new InvalidOperationException($"'{value}' is not a number of days."),
            _ => Identifier(value),
        };
    }

    private static void Result(StringBuilder sb, string message)
        => sb.AppendLine(CultureInfo.InvariantCulture, $"Write-Output {Q(ResultMarker + " " + message)}");
}
