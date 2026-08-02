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
$controlCenterProject = Join-Path $PSScriptRoot '..\..\src\BookStudio.ControlCenter\BookStudio.ControlCenter.csproj'
$certificateThumbprint = $null
$launchedProduct = $null

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

  Write-Output "[$([DateTimeOffset]::UtcNow.ToString('O'))] START installer-$Label"
  $process = Start-Process -FilePath $pwsh -ArgumentList $arguments -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  if (-not $process.WaitForExit(120000)) {
    try { $process.Kill($true) } catch { }
    throw "Installer validation phase '$Label' exceeded 120 seconds."
  }

  $out = if (Test-Path $stdout) { Get-Content $stdout -Raw } else { '' }
  $err = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
  if (-not [string]::IsNullOrWhiteSpace($out)) { Write-Output $out.TrimEnd() }
  if ($process.ExitCode -ne 0) {
    throw "Installer validation phase '$Label' failed with exit code $($process.ExitCode). $err"
  }
  Write-Output "[$([DateTimeOffset]::UtcNow.ToString('O'))] PASS installer-$Label"
}

function Get-FreeTcpPort {
  $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $listener.Start()
  try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
  finally { $listener.Stop() }
}

function Invoke-RealProductSmoke {
  $launcher = Join-Path $installRoot 'BookStudio.exe'
  if (-not (Test-Path $launcher -PathType Leaf)) { throw 'Real installed BookStudio launcher is missing.' }

  $port = Get-FreeTcpPort
  $url = "http://127.0.0.1:$port"
  $stdout = Join-Path $root 'real-product.stdout.log'
  $stderr = Join-Path $root 'real-product.stderr.log'
  $script:launchedProduct = Start-Process -FilePath $launcher -ArgumentList @('--urls', $url) -WorkingDirectory $installRoot -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr

  $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
  do {
    if ($script:launchedProduct.HasExited) {
      $err = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
      throw "Installed BookStudio exited before becoming healthy. $err"
    }
    try {
      $response = Invoke-RestMethod -Uri "$url/health/live" -TimeoutSec 3
      if ($response.status -eq 'live' -and $response.service -eq 'BookStudio.ControlCenter') {
        Write-Output 'Real installed BookStudio health smoke PASS'
        return
      }
    }
    catch {
      Start-Sleep -Milliseconds 500
    }
  } while ([DateTimeOffset]::UtcNow -lt $deadline)

  $err = if (Test-Path $stderr) { Get-Content $stderr -Raw } else { '' }
  throw "Installed BookStudio did not become healthy within 60 seconds. $err"
}

try {
  Write-Output 'Publishing the real BookStudio Control Center distributable.'
  New-Item -ItemType Directory -Force -Path $payload | Out-Null
  dotnet publish $controlCenterProject --configuration Release --no-restore --output $payload -p:AssemblyName=BookStudio -p:UseAppHost=true
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
  foreach ($requiredProductFile in @('BookStudio.exe', 'BookStudio.dll', 'BookStudio.deps.json', 'BookStudio.runtimeconfig.json')) {
    if (-not (Test-Path (Join-Path $payload $requiredProductFile) -PathType Leaf)) {
      throw "Real publish output is missing $requiredProductFile."
    }
  }

  Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $package -Force
  Copy-Item -LiteralPath $sourceInstaller -Destination $signedInstaller

  $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=BookStudio CI Validation' -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(1)
  $certificateThumbprint = $cert.Thumbprint
  if ($certificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Certificate creation returned an invalid thumbprint.' }

  Write-Output 'Signing the installer authority without mutating the runner trust store.'
  $signResult = Set-AuthenticodeSignature -FilePath $signedInstaller -Certificate $cert
  $signed = Get-AuthenticodeSignature -LiteralPath $signedInstaller
  if ($null -eq $signed.SignerCertificate -or $signed.SignerCertificate.Thumbprint -ne $certificateThumbprint) {
    throw 'Signed installer does not preserve the exact validation signer.'
  }
  if ($signed.Status -in @('NotSigned', 'HashMismatch')) {
    throw "Installer signing failed closed with status $($signed.Status)."
  }

  $expectedHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
  $env:BOOKSTUDIO_PROVIDER = 'opencode'
  $env:BOOKSTUDIO_MONTHLY_LIMIT_EUR = '25.50'
  $env:BOOKSTUDIO_PROVIDER_SECRET = 'validation-secret-must-not-remain-plaintext'
  $env:BOOKSTUDIO_VALIDATION_MODE = '1'
  $env:BOOKSTUDIO_VALIDATION_SIGNER_THUMBPRINT = $certificateThumbprint

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
  if ($evidence.signatureStatus -notin @('Valid', 'ValidForControlledValidation')) { throw 'Evidence does not preserve accepted signature status.' }
  if ($evidence.signerThumbprint -ne $certificateThumbprint) { throw 'Evidence signer thumbprint does not match.' }
  if ($evidence.provider -ne 'opencode' -or [decimal]$evidence.monthlyLimitEur -ne 25.50) { throw 'Provider or cost ceiling evidence is incorrect.' }
  if ($credential -match [regex]::Escape($env:BOOKSTUDIO_PROVIDER_SECRET)) { throw 'Provider credential was persisted in plaintext.' }

  Invoke-RealProductSmoke

  $evidenceHashBeforeRestart = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
  Invoke-InstallerValidation 'restart-idempotency'
  $evidenceHashAfterRestart = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
  if ($evidenceHashBeforeRestart -ne $evidenceHashAfterRestart) { throw 'Completed setup was repeated after restart.' }

  Write-Output 'VS-126 Windows installer E2E PASS'
}
finally {
  if ($null -ne $launchedProduct -and -not $launchedProduct.HasExited) {
    try { $launchedProduct.Kill($true) } catch { }
  }
  Remove-Item Env:BOOKSTUDIO_PROVIDER -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_MONTHLY_LIMIT_EUR -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_PROVIDER_SECRET -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_VALIDATION_MODE -ErrorAction SilentlyContinue
  Remove-Item Env:BOOKSTUDIO_VALIDATION_SIGNER_THUMBPRINT -ErrorAction SilentlyContinue
  if (-not [string]::IsNullOrWhiteSpace($certificateThumbprint)) {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$certificateThumbprint" -Force -ErrorAction SilentlyContinue
  }
  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
