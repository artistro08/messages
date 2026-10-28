<#
    Creates the self-signed certificate that signs the GMessages MSIX and trusts it on this PC.

    Outputs the certificate thumbprint (Build-Package.ps1 reads it from the store) and GMessages.cer
    in the repo root. Only needed on a PC without the signing certificate: reuses one that already
    exists. Run once, from an elevated PowerShell: trusting a certificate for sideloading means
    adding it to Local Machine > Trusted People.

    Depends on the PKI module that ships with Windows:
    https://learn.microsoft.com/powershell/module/pki/new-selfsignedcertificate
#>

#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    # Must match Publisher in src/GMessages.App/Package.appxmanifest
    [string] $Subject = 'CN=BF59F607-536B-4252-888F-7B3ED9D56F96'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

# Reuse An Existing Signing Certificate
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) -and $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' } |
    Select-Object -First 1

if (-not $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -FriendlyName 'GMessages package signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

# Trust It For Sideloading
$cerPath = Join-Path $root 'GMessages.cer'
Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null

Write-Output "Signing certificate: $($certificate.Thumbprint)"
