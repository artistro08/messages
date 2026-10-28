<#
    Builds the signed GMessages MSIX (Native AOT) for x64 and ARM64.

    Outputs .msix files under src/GMessages.App/AppPackages. Used to produce the installable app.
    Signs with the existing certificate whose subject is -CertificateSubject (see Signing.ps1); on a
    PC without it, run scripts/New-DevCertificate.ps1 once first.

    Depends on Visual Studio 2026 (MSBuild and the Native AOT C++ tools, found with vswhere:
    https://github.com/microsoft/vswhere) and the Windows App SDK MSIX tooling.
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string[]] $Platforms = @('x64', 'ARM64'),

    # Must match Publisher in src/GMessages.App/Package.appxmanifest
    [string] $CertificateSubject = 'CN=BF59F607-536B-4252-888F-7B3ED9D56F96'
)

$ErrorActionPreference = 'Stop'
$root    = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\GMessages.App\GMessages.App.csproj'

# Find The Signing Certificate And SignTool
. (Join-Path $PSScriptRoot 'Signing.ps1')
$certificate = Get-SigningCertificate $CertificateSubject
$signtool    = Get-SignTool

# The Manifest Publisher Must Match The Certificate, Or Signing Fails
$manifest  = [xml](Get-Content (Join-Path $root 'src\GMessages.App\Package.appxmanifest') -Raw)
$publisher = $manifest.Package.Identity.Publisher
if ($publisher -ne $certificate.Subject) {
    throw "Package.appxmanifest Publisher '$publisher' doesn't match the certificate subject '$($certificate.Subject)'."
}

# Find MSBuild (vswhere also has to be on PATH for the Native AOT linker)
$vswhereFolder = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH      = "$vswhereFolder;$env:PATH"
$msbuild       = & (Join-Path $vswhereFolder 'vswhere.exe') -latest -prerelease -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found. Install Visual Studio 2026 with the .NET desktop and C++ workloads.'
}

# Build And Package
foreach ($platform in $Platforms) {
    & $msbuild $project -restore `
        -p:Configuration=Release `
        -p:Platform=$platform `
        -p:PublishAot=true `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxBundle=Never `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
        -p:AppxPackageSigningTimestampServerUrl=$TimestampUrl `
        -p:AppxPackageSigningTimestampDigestAlgorithm=SHA256 `
        -p:AppExeSigningThumbprint=$($certificate.Thumbprint) `
        -p:AppExeSignTool=$($signtool.FullName) `
        -p:AppExeTimestampUrl=$TimestampUrl `
        -m -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Packaging failed for $platform ($LASTEXITCODE)."
    }
}

Get-ChildItem (Join-Path $root 'src\GMessages.App\AppPackages') -Recurse -Filter *.msix | ForEach-Object { Write-Output $_.FullName }
