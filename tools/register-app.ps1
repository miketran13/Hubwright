<#
.SYNOPSIS
  Registers Hubwright's app in your Entra ID tenant and connects Hubwright to it.

.DESCRIPTION
  Signs you in with your browser (Microsoft Graph PowerShell), then creates "Hubwright", a public
  client with a http://localhost redirect (browser and device-code sign-in) and a Windows broker
  redirect, and these delegated permissions:

    Read (always; scans, assessments, plans):
      Microsoft Graph: Sites.Read.All, Group.Read.All, User.Read.All, Reports.Read.All
      SharePoint:      AllSites.Read
    Write (only with -Write; used only when you build):
      Microsoft Graph: SharePointTenantSettings.ReadWrite.All (tenant sharing)
      SharePoint:      AllSites.FullControl (create sites, register and associate hubs, themes)

  The app is read-only unless you pass -Write (least privilege). When you are ready to build, run
  this script again with -AppId <id> -Write, or use Build → Add write permissions in the app. To go
  back to read-only after the build, run it with -AppId <id> and no -Write. Hubwright's writes are limited to an allow-list of documented requests that you preview and
  confirm. An admin must grant consent: review the app in the Entra admin center first.
  This script is the only step that writes to Entra ID, and you run it. See docs/permissions.md.

.PARAMETER Tenant
  Optional tenant prefix (contoso for contoso.sharepoint.com). Hubwright finds it from Graph at
  sign-in when you leave it out.

.PARAMETER AppId
  Update an existing Hubwright app instead of creating one: sets its permissions to the read list,
  plus the write list with -Write. Without -Write this removes the write permissions again.

.PARAMETER Write
  Add the write permissions (needed only to build). Without it, the app is read-only.

.PARAMETER Hubwright
  Path to hubwright(.exe), to save the connection. Defaults to hubwright on PATH.

.PARAMETER Pause
  Wait for Enter before closing (used when the desktop app opens this script in a new window).

.EXAMPLE
  ./register-app.ps1
  ./register-app.ps1 -AppId 00000000-0000-0000-0000-000000000000 -Write
  ./register-app.ps1 -AppId 00000000-0000-0000-0000-000000000000
#>
param(
    [string] $Tenant,
    [string] $AppId,
    [switch] $Write,
    [string] $Hubwright,
    [switch] $Pause
)
$ErrorActionPreference = 'Stop'

try {
    if (-not (Get-Module -ListAvailable Microsoft.Graph.Applications)) {
        Write-Host 'Installing Microsoft.Graph.Applications for the current user…'
        Install-Module Microsoft.Graph.Applications -Scope CurrentUser -Force
    }

    Import-Module Microsoft.Graph.Applications
    Write-Host 'Sign in with an account that can register apps (your browser opens)…'
    Connect-MgGraph -Scopes 'Application.ReadWrite.All' -NoWelcome
    $graph = '00000003-0000-0000-c000-000000000000'
    $sharePoint = '00000003-0000-0ff1-ce00-000000000000'
    function ScopeIds($resourceAppId, [string[]] $names) {
        $sp = Get-MgServicePrincipal -Filter "appId eq '$resourceAppId'"
        $names | ForEach-Object {
            $scopeName = $_
            $scope = $sp.Oauth2PermissionScopes | Where-Object Value -eq $scopeName
            if (-not $scope) { throw "Permission $scopeName not found on $($sp.DisplayName)." }
            @{ Id = $scope.Id; Type = 'Scope' }
        }
    }

    $graphScopes = @('Sites.Read.All', 'Group.Read.All', 'User.Read.All', 'Reports.Read.All')
    $sharePointScopes = @('AllSites.Read')
    if ($Write) {
        $graphScopes += 'SharePointTenantSettings.ReadWrite.All'
        $sharePointScopes += 'AllSites.FullControl'
    }

    $access = @(
        @{ ResourceAppId = $graph; ResourceAccess = @(ScopeIds $graph $graphScopes) },
        @{ ResourceAppId = $sharePoint; ResourceAccess = @(ScopeIds $sharePoint $sharePointScopes) }
    )

    if ($AppId) {
        $app = Get-MgApplication -Filter "appId eq '$AppId'"
        if (-not $app) { throw "No app with id $AppId in this tenant." }
        Update-MgApplication -ApplicationId $app.Id -RequiredResourceAccess $access
        Write-Host "Updated $($app.DisplayName) ($AppId): $($graphScopes -join ', ') on Graph; $($sharePointScopes -join ', ') on SharePoint."
    } else {
        $app = New-MgApplication -DisplayName 'Hubwright' -SignInAudience AzureADMyOrg `
            -IsFallbackPublicClient `
            -PublicClient @{ RedirectUris = @('http://localhost') } `
            -RequiredResourceAccess $access
        # The broker (WAM) redirect must carry the app's own id.
        Update-MgApplication -ApplicationId $app.Id -PublicClient @{ RedirectUris = @('http://localhost', "ms-appx-web://microsoft.aad.brokerplugin/$($app.AppId)") }
        New-MgServicePrincipal -AppId $app.AppId | Out-Null
        Write-Host "Created app $($app.AppId)."
    }

    $tenantId = (Get-MgContext).TenantId
    $consent = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/$($app.AppId)"
    Write-Host "Grant admin consent here (opening it now): $consent"
    Start-Process $consent

    if (-not $AppId) {
        $arguments = @('connect', '--client-id', $app.AppId, '--tenant-id', $tenantId)
        if ($Tenant) { $arguments += @('--tenant', $Tenant) }
        $exe = if ($Hubwright) { $Hubwright } elseif (Get-Command hubwright -ErrorAction SilentlyContinue) { 'hubwright' } else { $null }
        if ($exe) {
            & $exe @arguments
            Write-Host 'After consenting, sign in: hubwright login (or Connection → Sign in in the app).'
        } else {
            Write-Host "Then run: hubwright $($arguments -join ' ')"
        }
    } else {
        Write-Host $(if ($Write) { 'After consenting: hubwright build login (or Build → Grant write permissions in the app).' } else { 'The app is read-only now.' })
    }
}
catch {
    Write-Host "Failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
