<#
  Ondertekent een bestand (Authenticode) als er een certificaat is ingesteld.
  Zonder certificaat slaat het script zichzelf netjes over (exit 0), zodat de
  build blijft werken voordat er een code-signing certificaat is.

  Benodigde omgevingsvariabelen (uit GitHub Secrets):
    PFX_B64       - het .pfx-certificaat, base64-gecodeerd
    PFX_PASSWORD  - wachtwoord van het .pfx
  Optioneel:
    TIMESTAMP_URL - RFC3161 timestamp-server (anders een standaard)
#>
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:PFX_B64)) {
    Write-Host "Geen code-signing certificaat ingesteld - ondertekenen overgeslagen voor $Path"
    exit 0
}

if (-not (Test-Path $Path)) {
    Write-Error "Te ondertekenen bestand niet gevonden: $Path"
    exit 1
}

$pfxPath = Join-Path $env:RUNNER_TEMP 'codesign.pfx'
[IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:PFX_B64))

$securePwd = ConvertTo-SecureString $env:PFX_PASSWORD -AsPlainText -Force
$cert = Get-PfxCertificate -FilePath $pfxPath -Password $securePwd

$timestamp = if ([string]::IsNullOrWhiteSpace($env:TIMESTAMP_URL)) {
    'http://timestamp.digicert.com'
} else {
    $env:TIMESTAMP_URL
}

Set-AuthenticodeSignature -FilePath $Path -Certificate $cert `
    -TimestampServer $timestamp -HashAlgorithm SHA256 | Out-Null

Write-Host "Ondertekend: $Path  (timestamp: $timestamp)"
