#Requires -Version 7
<#
    sign-and-deploy.ps1
    Builds a DH5-signed plugin bundle for RotationHelper and deploys it to
    %LocalAppData%\DungeonHelperData\plugins\RotationHelper

    Produces:
      RotationHelper.dll            (Authenticode-signed with the author key)
      RotationHelper.deps.json
      RotationHelper.pem            (author public key)
      RotationHelper.manifest       (formatVersion=1, sha256 of every bundled file)
      RotationHelper.manifest.sig   (RSA-SHA256 PKCS#1 over the manifest bytes)
      plugin.metadata

    DH5 accepts a self-signed author cert (WinVerifyTrust CERT_E_UNTRUSTEDROOT is
    treated as valid). What it will NOT accept is a bundle without a valid
    entitlement — see README in this folder.
#>
[CmdletBinding()]
param(
    [string]$DllPath     = "H:\OPEN CODE WOW CHAT\RotationHelper\RotationHelper.DH5\bin\Release\net10.0-windows\RotationHelper.dll",
    [string]$DepsPath    = "H:\OPEN CODE WOW CHAT\RotationHelper\RotationHelper.DH5\bin\Release\net10.0-windows\RotationHelper.deps.json",
    [string]$SigningDir  = "H:\OPEN CODE WOW CHAT\RotationHelper\RotationHelper.DH5\signing",
    [string]$PluginName  = "RotationHelper",
    [string]$PluginId    = "B1C2D3E4-F5A6-7890-1234-56789ABCDEF0",
    [string]$AuthorName  = "Tanner",
    [string]$Version     = "1.0.0",
    [string]$DataDir     = "$env:LOCALAPPDATA\DungeonHelperData"
)

$ErrorActionPreference = 'Stop'
$openSsl = "C:\Program Files\Git\usr\bin\openssl.exe"
if (-not (Test-Path -LiteralPath $openSsl)) { throw "openssl not found at $openSsl" }

$pfx  = Join-Path $SigningDir "$PluginName.pfx"
$key  = Join-Path $SigningDir "$PluginName.key"
$pem  = Join-Path $SigningDir "$PluginName.pem"
foreach ($f in @($pfx, $key, $pem)) {
    if (-not (Test-Path -LiteralPath $f)) { throw "missing signing file: $f" }
}
if (-not (Test-Path -LiteralPath $DllPath)) { throw "missing built DLL: $DllPath" }

$stage = Join-Path $DataDir "plugins\$PluginName"
Write-Host "Staging into $stage"

# ---- 1. clean stage (preserve user profile data) --------------------------
if (Test-Path -LiteralPath $stage) {
    Get-ChildItem -LiteralPath $stage -Force | Where-Object { $_.Name -ne 'profiles' } | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
}

# ---- 2. copy plugin payload (host-provided assemblies are NOT shipped) ----
Copy-Item -LiteralPath $DllPath -Destination (Join-Path $stage "$PluginName.dll") -Force
Copy-Item -LiteralPath $DepsPath -Destination (Join-Path $stage "$PluginName.deps.json") -Force
Copy-Item -LiteralPath $pem -Destination (Join-Path $stage "$PluginName.pem") -Force

# ---- 3. plugin.metadata ---------------------------------------------------
$metadata = [ordered]@{
    PluginName      = $PluginName
    ProductInfoUrl  = $null
    UpdateSource    = $null
    CurrentVersion  = $Version
    AllowedVersions = @(
        [ordered]@{ Version = "$Version.0"; Url = $null; Checksum = $null }
    )
}
$metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage "plugin.metadata") -Encoding utf8NoBOM

# ---- 4. Authenticode-sign the DLL ----------------------------------------
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, "")
$dllOut = Join-Path $stage "$PluginName.dll"
$sig = Set-AuthenticodeSignature -FilePath $dllOut -Certificate $cert -HashAlgorithm SHA256
Write-Host "Authenticode status: $($sig.Status) - $($sig.StatusMessage)"

# Verify the signer fingerprint matches our author key
$signedCert = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($dllOut)
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($signedCert)
$der = $rsa.ExportSubjectPublicKeyInfo()
$sha = [System.Security.Cryptography.SHA256]::Create()
$signerFp = -join ($sha.ComputeHash($der) | ForEach-Object { $_.ToString("X2") })
Write-Host "Signer fingerprint: $signerFp"

# ---- 5. build the signed bundle manifest ---------------------------------
# hash every file in the stage except the manifest + its signature
$manifestPath = Join-Path $stage "$PluginName.manifest"
$sigPath      = Join-Path $stage "$PluginName.manifest.sig"
$entries = @()
Get-ChildItem -LiteralPath $stage -File | Where-Object {
    $_.Name -ne "$PluginName.manifest" -and $_.Name -ne "$PluginName.manifest.sig"
} | ForEach-Object {
    $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash  # uppercase
    $entries += [pscustomobject]@{
        path   = $_.Name.Replace('\','/')
        sha256 = $h
    }
}
$entries = $entries | Sort-Object path -CaseSensitive

$manifestObj = [ordered]@{
    formatVersion = 1
    pluginName    = $PluginName
    entries       = @($entries | ForEach-Object { [ordered]@{ path = $_.path; sha256 = $_.sha256 } })
}
$json = $manifestObj | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $manifestPath ($($entries.Count) entries)"

# ---- 6. sign the manifest (RSA SHA256 PKCS#1 over exact bytes) -----------
& $openSsl dgst -sha256 -sign $key -out $sigPath $manifestPath
# verify the detached signature round-trips
& $openSsl dgst -sha256 -verify $pem -signature $sigPath $manifestPath
if ($LASTEXITCODE -ne 0) { throw "manifest signature verification failed" }

# ---- 7. summary -----------------------------------------------------------
Write-Host ""
Write-Host "Bundle staged at: $stage"
Get-ChildItem -LiteralPath $stage | Select-Object Name, Length | Format-Table | Out-String | Write-Host
Write-Host "NEXT: drop a valid $PluginName.entitlement + $PluginName.entitlement.sig (DH-issued)" -ForegroundColor Yellow
Write-Host "      OR a machine entitlement at $DataDir\machine.entitlement (+ .sig)." -ForegroundColor Yellow


