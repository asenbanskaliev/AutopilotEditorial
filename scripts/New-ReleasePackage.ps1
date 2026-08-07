#Requires -Version 7.0
<#
.SYNOPSIS
    Packages the published BookStudio MCP servers into a versioned ZIP for distribution.

.DESCRIPTION
    Runs Publish-McpServers.ps1 (or uses an existing publish output), then bundles:
      - All 5 MCP server binaries (.runtime/publish/)
      - The Windows installer (install/windows/Install-BookStudio.ps1)
      - The opencode.json template
      - A generated SHA-256 manifest

    Output: .runtime/release/BookStudio-<version>.zip + BookStudio-<version>.sha256

.PARAMETER Version
    Release version string (e.g. 1.0.0). Defaults to reading from Directory.Build.props.

.PARAMETER OutputDir
    Directory where the ZIP and SHA256 are written. Defaults to .runtime/release.

.PARAMETER SkipPublish
    Skip dotnet publish (use existing .runtime/publish output).

.EXAMPLE
    .\New-ReleasePackage.ps1 -Version 1.0.0

.EXAMPLE
    .\New-ReleasePackage.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$OutputDir = '',
    [string]$PublishDir = '',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = Split-Path -Parent $PSScriptRoot
$publishDir = if ([string]::IsNullOrEmpty($PublishDir)) { Join-Path $repoRoot '.runtime' 'publish' } else { $PublishDir }

if ([string]::IsNullOrEmpty($OutputDir)) {
    $OutputDir = Join-Path $repoRoot '.runtime' 'release'
}

# Resolve version from Directory.Build.props when not provided
if ([string]::IsNullOrEmpty($Version)) {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (Test-Path -LiteralPath $propsPath) {
        $xml = [xml](Get-Content -LiteralPath $propsPath -Raw)
        $ver = $xml.SelectSingleNode('//VersionPrefix')?.InnerText
        if (-not [string]::IsNullOrEmpty($ver)) { $Version = $ver }
    }
    if ([string]::IsNullOrEmpty($Version)) { $Version = '0.1.0' }
}

Write-Host "BookStudio Release Packager" -ForegroundColor Cyan
Write-Host "  Version    : $Version"
Write-Host "  OutputDir  : $OutputDir"
Write-Host ""

# Step 1: Publish (unless skipped)
if (-not $SkipPublish) {
    $publishScript = Join-Path $PSScriptRoot 'Publish-McpServers.ps1'
    Write-Host "Running Publish-McpServers.ps1..." -ForegroundColor Yellow
    & $publishScript
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

# Step 2: Verify publish output exists
$servers = @('book-core','authoring','quality','production','ops')
foreach ($s in $servers) {
    $dir = Join-Path $publishDir $s
    if (-not (Test-Path -LiteralPath $dir)) {
        Write-Error "Missing publish output: $dir (run without -SkipPublish)"
        exit 1
    }
}
$workerDir = Join-Path $publishDir 'worker'
if (-not (Test-Path -LiteralPath $workerDir)) {
    Write-Error "Missing publish output: $workerDir (run without -SkipPublish)"
    exit 1
}

# Step 3: Stage package contents
$stagingDir = Join-Path $env:TEMP "bookstudio-release-$Version"
if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -Recurse -Force -LiteralPath $stagingDir
}
New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null

# Copy MCP server binaries
$serversDest = Join-Path $stagingDir 'servers'
Copy-Item -Recurse -Force -Path $publishDir -Destination $serversDest

# Place BookStudio.exe launcher (Worker executable) at package root
$workerExe = Join-Path $publishDir 'worker' 'BookStudio.Worker.exe'
if (Test-Path -LiteralPath $workerExe) {
    Copy-Item -Force -LiteralPath $workerExe -Destination (Join-Path $stagingDir 'BookStudio.exe')
} else {
    # Framework-dependent: copy the .dll and a thin bat launcher
    $workerDll = Join-Path $publishDir 'worker' 'BookStudio.Worker.dll'
    if (Test-Path -LiteralPath $workerDll) {
        Copy-Item -Force -LiteralPath $workerDll -Destination (Join-Path $stagingDir 'BookStudio.exe.dll')
        "@echo off`r`ndotnet `"%~dp0BookStudio.exe.dll`" %*" |
            Set-Content -LiteralPath (Join-Path $stagingDir 'BookStudio.exe') -Encoding ASCII
    } else {
        Write-Warning "Worker executable not found — BookStudio.exe placeholder written"
        '#!/bin/sh' | Set-Content -LiteralPath (Join-Path $stagingDir 'BookStudio.exe') -Encoding ASCII
    }
}

# Copy installer
$installerSrc = Join-Path $repoRoot 'install' 'windows' 'Install-BookStudio.ps1'
if (Test-Path -LiteralPath $installerSrc) {
    Copy-Item -Force -LiteralPath $installerSrc -Destination $stagingDir
}

# Write opencode template. The {{INSTALL_ROOT}} placeholder is materialized by
# Install-BookStudio.ps1 to the real install location, and {{WORKSPACE_ROOT}}
# to the resolved install project folder.
$openCodeTemplate = @{
    '$schema' = 'https://opencode.ai/config.json'
    mcp = @{
        'book-core'       = @{ type='local'; command=@('dotnet', '{{INSTALL_ROOT}}/servers/book-core/BookStudio.Mcp.dll', '--workspace-root', '{{WORKSPACE_ROOT}}') }
        'book-authoring'  = @{ type='local'; command=@('dotnet', '{{INSTALL_ROOT}}/servers/authoring/BookStudio.Mcp.Authoring.dll', '--workspace-root', '{{WORKSPACE_ROOT}}') }
        'book-quality'    = @{ type='local'; command=@('dotnet', '{{INSTALL_ROOT}}/servers/quality/BookStudio.Mcp.dll', '--workspace-root', '{{WORKSPACE_ROOT}}') }
        'book-production' = @{ type='local'; command=@('dotnet', '{{INSTALL_ROOT}}/servers/production/BookStudio.Mcp.dll', '--workspace-root', '{{WORKSPACE_ROOT}}') }
        'book-ops'        = @{ type='local'; command=@('dotnet', '{{INSTALL_ROOT}}/servers/ops/BookStudio.Mcp.dll', '--workspace-root', '{{WORKSPACE_ROOT}}') }
    }
    'x-bookstudio' = @{
        note = 'Edit {{WORKSPACE_ROOT}} to the folder where you write books, then copy this file there.'
    }
} | ConvertTo-Json -Depth 10
$openCodeTemplate | Set-Content -LiteralPath (Join-Path $stagingDir 'opencode.template.json') -Encoding UTF8

# Write version manifest
$manifest = @{
    product  = 'BookStudio MCP Servers'
    version  = $Version
    built    = (Get-Date -Format 'o')
    servers  = $servers
    dotnet   = 'net10.0'
} | ConvertTo-Json -Depth 5
$manifest | Set-Content -LiteralPath (Join-Path $stagingDir 'manifest.json') -Encoding UTF8

# Step 4: Create ZIP
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$zipName  = "BookStudio-mcp-$Version.zip"
$zipPath  = Join-Path $OutputDir $zipName

if (Test-Path -LiteralPath $zipPath) { Remove-Item -Force -LiteralPath $zipPath }

Write-Host "Creating $zipName..." -ForegroundColor Yellow
Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath -CompressionLevel Optimal

# Step 5: SHA-256
$hash     = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$hashFile = Join-Path $OutputDir "$zipName.sha256"
"$hash  $zipName" | Set-Content -LiteralPath $hashFile -Encoding ASCII

# Cleanup staging
Remove-Item -Recurse -Force -LiteralPath $stagingDir

Write-Host ""
Write-Host "Release package ready:" -ForegroundColor Cyan
Write-Host "  ZIP    : $zipPath"
Write-Host "  SHA256 : $hash"
Write-Host "  File   : $hashFile"
