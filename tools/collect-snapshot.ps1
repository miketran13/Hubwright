<#
.SYNOPSIS
  Collects a Hubwright snapshot with PnP PowerShell, for admins who can't (or prefer not to) consent
  to an app for Hubwright. Read-only: it only runs Get-* cmdlets.

.DESCRIPTION
  Writes snapshot.json in Hubwright's schema. Then run: hubwright import snapshot.json
  Whatever a cmdlet cannot read is recorded under scanIssues, so Hubwright reports it as
  "not assessed" rather than guessing.

.EXAMPLE
  ./collect-snapshot.ps1 -Tenant contoso -ClientId <your PnP app id> -MaxSites 300
#>
#Requires -Modules PnP.PowerShell
param(
    [Parameter(Mandatory)] [string] $Tenant,
    [Parameter(Mandatory)] [string] $ClientId,
    [int] $MaxSites = 200,
    [string] $Out = 'snapshot.json'
)
$ErrorActionPreference = 'Stop'
$root = "https://$Tenant.sharepoint.com"
$admin = "https://$Tenant-admin.sharepoint.com"
$issues = [System.Collections.Generic.List[object]]::new()
function Issue($area, $reason, $target = $null) { $issues.Add([ordered]@{ area = $area; reason = "$reason"; target = $target }) }
function Try-Get($area, [scriptblock] $read, $target = $null) {
    try { & $read } catch { Issue $area $_.Exception.Message $target; $null }
}

Connect-PnPOnline -Url $admin -Interactive -ClientId $ClientId
$adminConnection = Get-PnPConnection

# --- Tenant settings -------------------------------------------------------------------------
$t = Try-Get 'settings' { Get-PnPTenant }
$sharing = switch ("$($t.SharingCapability)") {
    'ExternalUserAndGuestSharing' { 'anyone' } 'ExternalUserSharingOnly' { 'newAndExistingGuests' }
    'ExistingExternalUserSharingOnly' { 'existingGuests' } 'Disabled' { 'onlyOrganization' } default { $null } }
$defaultLink = switch ("$($t.DefaultSharingLinkType)") { 'AnonymousAccess' { 'anyone' } 'Internal' { 'organization' } 'Direct' { 'specificPeople' } default { $null } }
function Access($v) { switch ("$v") { 'View' { 'view' } 'Edit' { 'edit' } default { $null } } }
$settings = [ordered]@{
    sharingLevel               = $sharing
    defaultLinkType            = $defaultLink
    defaultLinkPermission      = Access $t.DefaultLinkPermission
    anyoneLinkExpirationDays   = if ($null -ne $t.RequireAnonymousLinksExpireInDays) { [Math]::Max(0, [int]$t.RequireAnonymousLinksExpireInDays) } else { $null }
    anyoneFileLinkPermission   = Access $t.FileAnonymousLinkType
    anyoneFolderLinkPermission = Access $t.FolderAnonymousLinkType
    guestAccessExpirationDays  = if ($t.ExternalUserExpirationRequired) { [int]$t.ExternalUserExpireInDays } elseif ($null -ne $t.ExternalUserExpirationRequired) { 0 } else { $null }
}

# --- Themes ----------------------------------------------------------------------------------
$themes = @(Try-Get 'themes' { Get-PnPTenantTheme } | Where-Object { $_ } | ForEach-Object {
    $palette = [ordered]@{}
    foreach ($k in $_.Palette.Keys) { $palette[$k] = $_.Palette[$k] }
    [ordered]@{ name = $_.Name; isInverted = [bool]$_.IsInverted; palette = $palette }
})

# --- Hubs, home site, sites ------------------------------------------------------------------
$hubs = @(Try-Get 'hubs' { Get-PnPHubSite } | Where-Object { $_ } | ForEach-Object {
    [ordered]@{ id = "$($_.ID)"; title = $_.Title; siteUrl = $_.SiteUrl.TrimEnd('/'); parentHubId = if ($_.ParentHubSiteId -and "$($_.ParentHubSiteId)" -ne [Guid]::Empty.ToString()) { "$($_.ParentHubSiteId)" } else { $null }; navigation = @() }
})
$homeUrl = Try-Get 'home-site' { Get-PnPHomeSite }

function Kind($template) {
    switch -Wildcard ($template) {
        'SITEPAGEPUBLISHING*' { 'communication' } 'GROUP*' { 'teamGroup' } 'TEAMCHANNEL*' { 'channel' }
        'STS#3' { 'teamNoGroup' } 'STS#*' { 'classic' } 'BLOG*' { 'classic' } 'BDR*' { 'classic' } 'SRCHCEN*' { 'classic' }
        default { 'other' } }
}

$tenantSites = @(Try-Get 'sites' { Get-PnPTenantSite -Detailed } | Where-Object { $_ -and $_.Url -notmatch '-my\.sharepoint\.com' -and $_.Template -notmatch 'SPSPERS|APPCATALOG|EHS#|SRCHCEN|POINTPUBLISHING|SPSMSITEHOST|REDIRECTSITE' })
$sites = [System.Collections.Generic.List[object]]::new()
foreach ($s in $tenantSites) {
    $url = $s.Url.TrimEnd('/')
    $hubId = if ($s.HubSiteId -and "$($s.HubSiteId)" -ne [Guid]::Empty.ToString()) { "$($s.HubSiteId)" } else { $null }
    $sites.Add([ordered]@{
        url = $url; title = $s.Title; template = $s.Template; kind = Kind $s.Template
        isHub = [bool]($hubs | Where-Object { $_.siteUrl -eq $url }); isHomeSite = ($homeUrl -and $url -eq "$homeUrl".TrimEnd('/'))
        hubId = $hubId; sensitivityLabel = if ($s.SensitivityLabel -and "$($s.SensitivityLabel)" -ne [Guid]::Empty.ToString()) { "$($s.SensitivityLabel)" } else { $null }
        storageUsedMb = [long]$s.StorageUsageCurrent; lastActivity = $s.LastContentModifiedDate.ToString('o')
        lockState = switch ("$($s.LockState)") { 'ReadOnly' { 'readOnly' } 'NoAccess' { 'noAccess' } default { 'unlocked' } }
        sharingLevel = switch ("$($s.SharingCapability)") { 'ExternalUserAndGuestSharing' { 'anyone' } 'ExternalUserSharingOnly' { 'newAndExistingGuests' } 'ExistingExternalUserSharingOnly' { 'existingGuests' } 'Disabled' { 'onlyOrganization' } default { $null } }
        navigation = @(); permissions = [ordered]@{}; content = [ordered]@{}
    })
}
$sites | Where-Object isHub | ForEach-Object { $_.hubId = ($hubs | Where-Object siteUrl -eq $_.url).id }

# --- Site details (navigation, owners, subsites, lists, broad grants) ------------------------
function Nodes($nodes, $depth) {
    @($nodes | ForEach-Object {
        $children = if ($depth -lt 3) { Nodes (Get-PnPNavigationNode -Id $_.Id).Children ($depth + 1) } else { @() }
        [ordered]@{ title = $_.Title; url = if ($_.Url -and $_.Url -ne 'http://linkless.header/') { $_.Url } else { $null }; children = $children }
    })
}

$detailed = $sites | Sort-Object { -not ($_.isHub -or $_.isHomeSite) }, { $_.lastActivity } -Descending | Select-Object -First $MaxSites
$i = 0
foreach ($site in $detailed) {
    $i++
    Write-Progress -Activity 'Reading sites' -Status $site.url -PercentComplete (100 * $i / [Math]::Max(1, $detailed.Count))
    try { Connect-PnPOnline -Url $site.url -Interactive -ClientId $ClientId } catch { Issue 'site-detail' $_.Exception.Message $site.url; continue }

    Try-Get 'site-detail' {
        $web = Get-PnPWeb -Includes MegaMenuEnabled, WebTemplate, Configuration
        $site.menuStyle = if ($web.MegaMenuEnabled) { 'mega' } else { 'cascading' }
        $site.subsiteCount = @(Get-PnPSubWeb).Count
        $owners = Get-PnPGroup -AssociatedOwnerGroup
        $site.ownerCount = @(Get-PnPGroupMember -Group $owners).Count
    } $site.url | Out-Null

    Try-Get 'navigation' {
        $location = if ($site.kind -eq 'communication' -or $site.isHub) { 'TopNavigationBar' } else { 'QuickLaunch' }
        $site.navigation = Nodes (Get-PnPNavigationNode -Location $location) 1
        $titles = @($site.navigation | ForEach-Object title)
        $site.content.hasDefaultNavigation = ($site.kind -eq 'communication' -and $titles.Count -gt 0 -and -not ($titles | Where-Object { $_ -notin 'Home', 'Documents', 'Pages', 'Site contents' }))
        if ($site.isHub) { ($hubs | Where-Object siteUrl -eq $site.url).navigation = $site.navigation }
    } $site.url | Out-Null

    Try-Get 'content' {
        $lists = @(Get-PnPList | Where-Object { -not $_.Hidden })
        $libraries = @($lists | Where-Object { $_.BaseType -eq 'DocumentLibrary' })
        $site.content.libraries = $libraries.Count
        $site.content.lists = $lists.Count - $libraries.Count
        $site.content.largestListItems = ($lists | Measure-Object ItemCount -Maximum).Maximum
        $site.content.librariesWithoutVersioning = @($libraries | Where-Object { -not $_.EnableVersioning }).Count
        $site.content.usesInfoPath = [bool]($lists | Where-Object { $_.BaseTemplate -eq 115 })
    } $site.url | Out-Null

    Try-Get 'permissions' {
        $web = Get-PnPWeb
        $assignments = Get-PnPProperty -ClientObject $web -Property RoleAssignments
        $logins = foreach ($a in $assignments) { (Get-PnPProperty -ClientObject $a -Property Member).LoginName }
        $site.permissions.eeeuGrants = @($logins | Where-Object { $_ -match 'spo-grid-all-users' }).Count
        $site.permissions.everyoneGrants = @($logins | Where-Object { $_ -like 'c:0(.s|true*' }).Count
        $site.isPublic = ($site.permissions.eeeuGrants + $site.permissions.everyoneGrants) -gt 0
    } $site.url | Out-Null
}
foreach ($skipped in ($sites | Where-Object { $_ -notin $detailed })) { Issue 'site-detail' "Not read in detail (-MaxSites $MaxSites)." $skipped.url }
Issue 'global-navigation' 'The PnP collector does not read the app bar global navigation yet.'

$snapshot = [ordered]@{
    schema = 1; tenant = $Tenant; capturedAt = (Get-Date).ToUniversalTime().ToString('o'); source = 'import'
    settings = $settings; sites = $sites; hubs = $hubs; themes = $themes; scanIssues = $issues
}
$snapshot | ConvertTo-Json -Depth 20 | Set-Content -Path $Out -Encoding utf8
Write-Host "Wrote ${Out}: $($sites.Count) sites, $($hubs.Count) hubs, $($issues.Count) not read. Next: hubwright import $Out"
