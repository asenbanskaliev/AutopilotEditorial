[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path $env:RUNNER_TEMP ("bookstudio-installer-e2e-" + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $root 'payload'
$installRoot = Join-Path $root 'installed'
$package = Join-Path $root 'BookStudio.zip'
$signedInstaller = Join-Path $root 'Install-BookStudio.ps1'
$sourceInstaller = Join-Path $PSScriptRoot '..\..\install\windows\Install-BookStudio.ps1'
$cert = $null

function Invoke-InstallerValidation([string]$Label) {
  $stdout = Join-Path $root "$Label.stdout.log"
  $stderr = Join-Path $root "$Label.stderr.log"
  $pwsh = (Get-Command pwsh -ErrorAction Stop).Source
  $arguments = @(
    '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $signedInstaller),
    '-PackagePath', ('"{0}"' -f $package),
    '-ExpectedSha256', $expectedHash,
    '-InstallRoot', ('"{0}"' -f $installRoot),
    '-NonInteractive', '-NoLaunchForValidation'
  ) -join ' '

  Write-Output "Starting installer validation phase: $Label"
  $process = Start-Process -FilePath $pwsh -ArgumentList $arguments -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  if (-not $process.WaitForExit(120000)) {
    $process.Kill($true)
    throw "Installer validation phase '$Label' exceeded 120 seconds."
  }

  $out = if (Test-Path $stdout) { Get-Content $stdout -Raw } else { '' }
  $err = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
  if (-not [string]::IsNullOrWhiteSpace($out)) { Write-Output $out.TrimEnd() }
  if ($process.ExitCode -ne 0) {
    throw "Installer validation phase '$Label' failed with exit code $($process.ExitCode). $err"
  }
}

try {
  Write-Output 'Preparing deterministic installer payload.'
  New-Item -ItemType Directory -Force -Path $payload | Out-Null
  Set-Content -LiteralPath (Join-Path $payload 'BookStudio.exe') -Value 'validation launcher placeholder' -Encoding UTF8
  Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $package -Force
  Copy-Item -LiteralPath $sourceInstaller -Destination $signedInstaller

  Write-Output 'Creating and trusting the ephemeral code-signing certificate.'
  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=BookStudio CI Validation' -CertStoreLocation 'Cert:\CurrentUser\My'
  $certificateFile = Join-Path $root 'bookstudio-validation.cer'
  Export-Certificate -Cert $cert -FilePath $certificateFile | Out-Null
  Import-Certificate -FilePath $certificateFile -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null

  Write-Output 'Signing the installer authority.'
  $signResult = Set-AuthenticodeSignature -FilePath $signedInstaller -Certificate $cert
  if ($signResult.Status -ne 'Valid') { throw "Unable to create a valid installer signature: $($signResult.Status)" }

  $expectedHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
  $env:BOOKSTUDIO_PROVIDER = 'opencode'
  $env:BOOKSTUDIO_MONTHLY_LIMIT_EUR = '25.50'
  $env:BOOKSTUDIO_PROVIDER_SECRET = 'validation-secret-must-not-remain-plaintext'
  $env:BOOKSTUDIO_VALIDATION_MODE = '1'

  Invoke-InstallerValidation 'initial-install'

  $statePath = Join-Path $installRoot 'state\first-run.json'
  $evidencePath = Join-Path $installRoot 'evidence\installation.json'
  $credentialPath = Join-Path $installRoot 'secrets\provider.credential'
  foreach ($required in @($statePath, $evidencePath, $credentialPath)) {
    if (-not (Test-Path $required -PathType Leaf)) { throw "Missing durable installer output: $required" }
  }

  $state = Get-Content $statePath -Raw | ConvertFrom-Json
  $evidence = Get-Content $evidencePath -Raw | ConvertFrom-Json
  $credential = Get-Content $credentialPath -Raw
  if ($state.completed -ne $true -or $state.phase -ne 'ready') { throw 'Installer did not reach durable ready state.' }
  if ($evidence.packageSha256 -ne $expectedHash.ToLowerInvariant()) { throw 'Evidence package digest does not match.' }
  if ($evidence.signatureStatus -ne 'Valid') { throw 'Evidence does not preserve valid signature status.' }
  if ($evidence.provider -ne 'opencode' -or [decimal]$evidence.monthlyLimitEur -ne 25.50) { throw 'Provider or cost ceiling evidence is incorrect.' }
  if ($credential -match [regex]::Escape($env:BOOKSTUDIO_PROVIDER_SECRET)) { throw 'Provider credential was persisted in plaintext.' }

  $evidenceHashBeforeRestart = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
  Invoke-InstallerValidation 'restart-idempotency'
  $evidenceHashAfterRestart = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
  if ($evidenceHashBeforeRestart -ne $evidenceHashAfterRestart) { throw 'Completed setup was repeated after restart.' }

  Write-Output 'VS-126 Windows installer E2E PASS'
}
finally {
  Remove-Item Env:BOOKSTUDIO_PROVIDER -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_MONTHLY_LIMIT_EUR -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_PROVIDER_SECRET -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_VALIDATION_MODE -ErrorAction SilentlyContinue
  if ($null -ne $cert) {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
  }
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
