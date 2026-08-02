[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PackagePath,
  [Parameter(Mandatory=$true)][string]$ExpectedSha256,
  [string]$InstallRoot = "$env:LOCALAPPDATA\BookStudio",
  [int]$MaxRepairAttempts = 2,
  [switch]$NonInteractive,
  [switch]$NoLaunchForValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-AtomicJson([string]$Path, [object]$Value) {
  $dir = Split-Path -Parent $Path
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
  $tmp = "$Path.tmp"
  $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $tmp -Encoding UTF8
  Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Assert-WithinRoot([string]$Candidate, [string]$Root) {
  $fullCandidate = [IO.Path]::GetFullPath($Candidate)
  $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  if (-not $fullCandidate.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Path escapes installation root: $Candidate"
  }
}

function Protect-Secret([string]$PlainText) {
  $secure = ConvertTo-SecureString $PlainText -AsPlainText -Force
  return ConvertFrom-SecureString $secure
}

if ($NoLaunchForValidation -and $env:BOOKSTUDIO_VALIDATION_MODE -ne '1') {
  throw 'NoLaunchForValidation is restricted to the controlled validation environment.'
}

$installerSignature = Get-AuthenticodeSignature -LiteralPath $PSCommandPath
$signatureStatus = $installerSignature.Status.ToString()
$controlledValidation = $env:BOOKSTUDIO_VALIDATION_MODE -eq '1'
if ($signatureStatus -ne 'Valid') {
  $expectedThumbprint = $env:BOOKSTUDIO_VALIDATION_SIGNER_THUMBPRINT
  $actualThumbprint = $installerSignature.SignerCertificate.Thumbprint
  $isControlledSelfSignedValidation =
    $controlledValidation -and
    $signatureStatus -eq 'UnknownError' -and
    -not [string]::IsNullOrWhiteSpace($expectedThumbprint) -and
    $actualThumbprint -eq $expectedThumbprint -and
    $installerSignature.SignerCertificate.NotBefore -le (Get-Date) -and
    $installerSignature.SignerCertificate.NotAfter -ge (Get-Date)

  if (-not $isControlledSelfSignedValidation) {
    throw "Installer signature is not valid: $signatureStatus"
  }

  $signatureStatus = 'ValidForControlledValidation'
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ([IO.Path]::GetExtension($package) -ne '.zip') { throw 'Package must be a ZIP archive.' }
$actualHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) { throw 'Package SHA-256 mismatch.' }

$statePath = Join-Path $InstallRoot 'state\first-run.json'
$evidencePath = Join-Path $InstallRoot 'evidence\installation.json'
$credentialPath = Join-Path $InstallRoot 'secrets\provider.credential'
Assert-WithinRoot $statePath $InstallRoot
Assert-WithinRoot $evidencePath $InstallRoot
Assert-WithinRoot $credentialPath $InstallRoot

$state = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else {
  [ordered]@{ version=1; phase='verified'; repairAttempts=0; completed=$false; updatedAt=(Get-Date).ToString('o') }
}

if ($state.completed -eq $true) {
  Write-Output 'BookStudio is already configured.'
  exit 0
}

try {
  if ($state.phase -eq 'verified') {
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    Expand-Archive -LiteralPath $package -DestinationPath $InstallRoot -Force
    $state.phase = 'installed'; $state.updatedAt = (Get-Date).ToString('o'); Write-AtomicJson $statePath $state
  }

  if ($state.phase -eq 'installed') {
    $provider = if ($NonInteractive) { $env:BOOKSTUDIO_PROVIDER } else { Read-Host 'Proveedor (opencode/openai)' }
    if ([string]::IsNullOrWhiteSpace($provider)) { throw 'Provider is required.' }
    $monthlyLimit = if ($NonInteractive) { $env:BOOKSTUDIO_MONTHLY_LIMIT_EUR } else { Read-Host 'Límite mensual máximo en EUR' }
    [decimal]$parsedLimit = 0
    if (-not [decimal]::TryParse($monthlyLimit, [Globalization.NumberStyles]::Number, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedLimit) -or $parsedLimit -lt 0) {
      throw 'A valid non-negative monthly limit is required.'
    }
    $secret = if ($NonInteractive) { $env:BOOKSTUDIO_PROVIDER_SECRET } else { Read-Host 'Credencial del proveedor' -AsSecureString | ConvertFrom-SecureString }
    if ($NonInteractive) {
      if ([string]::IsNullOrWhiteSpace($secret)) { throw 'Credential is required.' }
      $secret = Protect-Secret $secret
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $credentialPath -Parent) | Out-Null
    Set-Content -LiteralPath "$credentialPath.tmp" -Value $secret -Encoding UTF8
    Move-Item -LiteralPath "$credentialPath.tmp" -Destination $credentialPath -Force
    $state.provider = $provider; $state.monthlyLimitEur = $parsedLimit; $state.phase = 'configured'; $state.updatedAt = (Get-Date).ToString('o'); Write-AtomicJson $statePath $state
  }

  if ($state.phase -eq 'configured') {
    $launcher = Join-Path $InstallRoot 'BookStudio.exe'
    Assert-WithinRoot $launcher $InstallRoot
    if (-not (Test-Path $launcher -PathType Leaf)) { throw 'Installed launcher was not found.' }
    $state.phase = 'ready'; $state.completed = $true; $state.updatedAt = (Get-Date).ToString('o'); Write-AtomicJson $statePath $state
    Write-AtomicJson $evidencePath ([ordered]@{
      schemaVersion=1; packageSha256=$actualHash; signer=$installerSignature.SignerCertificate.Subject;
      signerThumbprint=$installerSignature.SignerCertificate.Thumbprint; signatureStatus=$signatureStatus;
      installRoot=[IO.Path]::GetFullPath($InstallRoot);
      provider=$state.provider; monthlyLimitEur=$state.monthlyLimitEur; credentialStorage='Windows-DPAPI-current-user';
      completedAt=(Get-Date).ToString('o'); repairAttempts=$state.repairAttempts
    })
    if (-not $NoLaunchForValidation) { Start-Process -FilePath $launcher }
  }
}
catch {
  $state.repairAttempts = [int]$state.repairAttempts + 1
  $state.lastError = $_.Exception.Message
  $state.updatedAt = (Get-Date).ToString('o')
  Write-AtomicJson $statePath $state
  if ($state.repairAttempts -gt $MaxRepairAttempts) { throw "Repair limit exceeded. Manual review required. $($_.Exception.Message)" }
  throw
}
