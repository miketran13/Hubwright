<#
.SYNOPSIS
  Builds and starts Hubwright Desktop.
.PARAMETER Preview
  Uses ~/.hubwright-preview with the two sample tenants loaded, so your real data is untouched.
.PARAMETER Page
  Opens a page: start, tenant, history, dashboard, findings, structure, reports, proposal, blueprint,
  preview, ask, proposals, skills, templates, facts, audit, settings.
#>
param(
    [switch] $Preview,
    [string] $Page,
    [switch] $NoBuild,
    [string] $Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

Get-Process Hubwright.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not $NoBuild) {
    dotnet build "$root/src/Hubwright.Desktop" -c $Configuration -r $rid -v q
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$cli = Get-ChildItem "$root/src/Hubwright.Cli/bin/$Configuration" -Recurse -Filter hubwright.exe | Select-Object -First 1
if ($Preview) {
    $env:HUBWRIGHT_HOME = Join-Path $HOME '.hubwright-preview'
    & $cli.FullName sample fabrikam | Out-Null
    & $cli.FullName sample northwind | Out-Null
    & $cli.FullName blueprint new --tenant contoso --answers "$root/docs/examples/answers.json" | Out-Null
    # A ready-made intranet proposal for the messy sample, seeded from its scan.
    $answers = Join-Path $env:HUBWRIGHT_HOME 'fabrikam-answers.json'
    & $cli.FullName proposal answers --tenant fabrikam --out $answers 2>$null | Out-Null
    & $cli.FullName proposal new --tenant fabrikam --answers $answers | Out-Null
}

$exe = Get-ChildItem "$root/src/Hubwright.Desktop/bin" -Recurse -Filter Hubwright.Desktop.exe | Where-Object { $_.FullName -match "\\$Configuration\\" } | Select-Object -First 1
$arguments = @()
if ($Page) { $arguments += @('--page', $Page) }
if ($Preview) { $arguments += @('--tenant', 'fabrikam') }
Start-Process $exe.FullName -ArgumentList $arguments
