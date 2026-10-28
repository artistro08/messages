<#
    Shared signing setup, dot-sourced by Build-Package.ps1: finds the code signing certificate and
    SignTool, and names the timestamp server.

    Every signature is timestamped, so packages stay valid after the certificate expires. SignTool
    comes from the Windows SDK build tools NuGet package
    (https://www.nuget.org/packages/Microsoft.Windows.SDK.BuildTools).
#>

# RFC 3161 Timestamp Server
$TimestampUrl = 'http://timestamp.digicert.com'

# Default Certificate Subject: the owner's existing signing certificate (shared with Magic Buds). Must match
# Publisher in src/GMessages.App/Package.appxmanifest.
$DefaultSigningSubject = 'CN=BF59F607-536B-4252-888F-7B3ED9D56F96'

# The code signing certificate: the one pinned in GMESSAGES_SIGNING_THUMBPRINT, or the only valid code signing
# certificate with the given subject in the personal store. Several matches is an error rather than a guess.
function Get-SigningCertificate([string] $Subject = $DefaultSigningSubject) {
    $valid = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.NotAfter -gt (Get-Date) -and $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3' }

    if ($env:GMESSAGES_SIGNING_THUMBPRINT) {
        $pinned = $valid | Where-Object { $_.Thumbprint -eq $env:GMESSAGES_SIGNING_THUMBPRINT }
        if (-not $pinned) {
            throw "No valid code signing certificate with thumbprint $env:GMESSAGES_SIGNING_THUMBPRINT."
        }
        return $pinned
    }

    $candidates = @($valid | Where-Object { $_.Subject -eq $Subject })
    if ($candidates.Count -eq 0) {
        throw "No valid $Subject signing certificate in Cert:\CurrentUser\My. Run scripts/New-DevCertificate.ps1 from an elevated PowerShell first."
    }
    if ($candidates.Count -gt 1) {
        throw "Several $Subject signing certificates. Set GMESSAGES_SIGNING_THUMBPRINT to the one to use."
    }
    return $candidates[0]
}

# SignTool from the newest Windows SDK in the build tools package, compared as versions
function Get-SignTool {
    $signtool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' -and $_.Directory.Parent.Name -as [version] } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if (-not $signtool) {
        throw 'SignTool not found. Restore the solution once so the Windows SDK build tools package is downloaded.'
    }
    return $signtool
}
