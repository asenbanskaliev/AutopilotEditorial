#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes all 5 BookStudio MCP servers as self-contained deployments.

.DESCRIPTION
    Runs `dotnet publish` for each MCP server project targeting the configured
    runtime. Outputs to .runtime/publish/<server-name>/ by default.

.PARAMETER Runtime
    .NET RID for the target platform. Defaults to win-x64.

.PARAMETER OutputRoot
    Root directory for publish output. Defaults to .runtime/publish relative to repo root.

.PARAMETER Configuration
    Build configuration. Defaults to Release.

.PARAMETER SelfContained
    Publish as self-contained (bundles .NET runtime). Defaults to false (framework-dependent).

.EXAMPLE
    .\Publish-McpServers.ps1

.EXAMPLE
    .\Publish-McpServers.ps1 -Runtime win-x64 -SelfContained
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot = '',
    [string]$Configuration = 'Release',
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot '.runtime' 'publish'
}

# Detect if publish target is locked (MCP servers running) and use a staging dir instead
$lockDetected = $false
$testDll = Join-Path $OutputRoot 'book-core' 'BookStudio.Mcp.dll'
if (Test-Path -LiteralPath $testDll) {
    try {
        $stream = [IO.File]::Open($testDll, 'Open', 'ReadWrite', 'None')
        $stream.Dispose()
    } catch {
        $lockDetected = $true
    }
}

if ($lockDetected) {
    $stagingRoot = Join-Path $repoRoot '.runtime' 'publish-next'
    Write-Host "  NOTE: publish target is locked by running MCP servers." -ForegroundColor DarkYellow
    Write-Host "  Publishing to staging: $stagingRoot" -ForegroundColor DarkYellow
    Write-Host "  Swap with publish/ after stopping MCP servers, or use -OutputRoot to choose a path." -ForegroundColor DarkYellow
    $OutputRoot = $stagingRoot
}

$servers = @(
    @{ Project = 'src/BookStudio.Mcp/BookStudio.Mcp.csproj';                         OutDir = 'book-core'   }
    @{ Project = 'src/BookStudio.Mcp.Authoring/BookStudio.Mcp.Authoring.csproj';     OutDir = 'authoring'   }
    @{ Project = 'src/BookStudio.Mcp.Quality/BookStudio.Mcp.Quality.csproj';         OutDir = 'quality'     }
    @{ Project = 'src/BookStudio.Mcp.Production/BookStudio.Mcp.Production.csproj';   OutDir = 'production'  }
    @{ Project = 'src/BookStudio.Mcp.Ops/BookStudio.Mcp.Ops.csproj';                 OutDir = 'ops'         }
    @{ Project = 'src/BookStudio.Worker/BookStudio.Worker.csproj';                   OutDir = 'worker'      }
)

Write-Host "BookStudio MCP Server Publisher" -ForegroundColor Cyan
Write-Host "  Configuration : $Configuration"
Write-Host "  Runtime       : $Runtime"
Write-Host "  SelfContained : $SelfContained"
Write-Host "  OutputRoot    : $OutputRoot"
Write-Host ""

$failed = @()

foreach ($server in $servers) {
    $projectPath = Join-Path $repoRoot $server.Project
    $outDir      = Join-Path $OutputRoot $server.OutDir

    Write-Host "Publishing $($server.OutDir)..." -ForegroundColor Yellow

    $publishArgs = @(
        'publish', $projectPath,
        '--configuration', $Configuration,
        '--output', $outDir,
        '--no-restore',
        '-v', 'q'
    )

    if ($SelfContained) {
        $publishArgs += '--self-contained', 'true', '--runtime', $Runtime
    }

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Error "  FAILED: $($server.Project) (exit $LASTEXITCODE)"
        $failed += $server.OutDir
    } else {
        Write-Host "  OK -> $outDir" -ForegroundColor Green
    }
}

Write-Host ""

if ($failed.Count -gt 0) {
    Write-Error "Publish failed for: $($failed -join ', ')"
    exit 1
}

Write-Host "All 5 MCP servers published successfully." -ForegroundColor Cyan
