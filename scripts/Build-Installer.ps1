<#
    Builds the signed Messages MSI (Native AOT) for x64 and ARM64.

    Outputs artifacts\Messages-<Version>-x64.msi and -arm64.msi, the installers a GitHub release ships and the app's
    updater downloads. Signs the exe and the MSI with the existing certificate whose subject is -CertificateSubject
    (see Signing.ps1); on a PC without it, run scripts/New-DevCertificate.ps1 once first. The version comes from
    src/Directory.Build.props.

    Depends on Visual Studio 2026 (MSBuild and the Native AOT C++ tools, found with vswhere:
    https://github.com/microsoft/vswhere), the .NET SDK, and the WiX Toolset, which comes in as a NuGet package:
    https://wixtoolset.org/docs/intro/
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string[]] $Platforms = @('x64', 'ARM64'),

    [string] $CertificateSubject = 'CN=BF59F607-536B-4252-888F-7B3ED9D56F96'
)

$ErrorActionPreference = 'Stop'
$root      = Resolve-Path (Join-Path $PSScriptRoot '..')
$project   = Join-Path $root 'src\GMessages.App\GMessages.App.csproj'
$installer = Join-Path $root 'installer\Messages.Installer.wixproj'
$artifacts = Join-Path $root 'artifacts'
$nuget     = Join-Path $env:USERPROFILE '.nuget\packages'

# Find The Signing Certificate And SignTool
. (Join-Path $PSScriptRoot 'Signing.ps1')
$certificate = Get-SigningCertificate $CertificateSubject
$signtool    = Get-SignTool

# App Name, Version And The Prerequisite Package Versions: one place each, in src/
$props    = [xml](Get-Content (Join-Path $root 'src\Directory.Build.props') -Raw)
$name     = ($props.Project.PropertyGroup.AppDisplayName | Where-Object { $_ }) | Select-Object -First 1
$version  = ($props.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
$packages = [xml](Get-Content (Join-Path $root 'src\Directory.Packages.props') -Raw)
$runtime  = ($packages.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'Microsoft.WindowsAppSDK.Runtime').Version
$winui    = ($packages.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'Microsoft.UI.Xaml').Version

# Find MSBuild (vswhere also has to be on PATH for the Native AOT linker)
$vswhereFolder = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH      = "$vswhereFolder;$env:PATH"
$msbuild       = & (Join-Path $vswhereFolder 'vswhere.exe') -latest -prerelease -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found. Install Visual Studio 2026 with the .NET desktop and C++ workloads.'
}

foreach ($platform in $Platforms) {
    $arch          = $platform.ToLower()
    $publish       = Join-Path $artifacts "publish\$platform"
    $prerequisites = Join-Path $artifacts "prerequisites\$platform"
    $msiOut        = Join-Path $artifacts "msi\$platform"
    foreach ($folder in $publish, $prerequisites) {
        if (Test-Path $folder) {
            Remove-Item $folder -Recurse -Force
        }
        New-Item -ItemType Directory -Path $folder | Out-Null
    }

    # Publish The App: Native AOT, the exe signed right after the native link (GMessages.App.csproj)
    & $msbuild $project -restore -t:Publish `
        -p:Configuration=Release `
        -p:Platform=$platform `
        -p:PublishDir="$publish\\" `
        -p:AppExeSigningThumbprint=$($certificate.Thumbprint) `
        -p:AppExeSignTool=$($signtool.FullName) `
        -p:AppExeTimestampUrl=$TimestampUrl `
        -m -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing failed for $platform ($LASTEXITCODE)."
    }

    # Prerequisites: the Windows App SDK runtime packages and WinUI 2.8, from the restored NuGet packages
    Copy-Item (Join-Path $nuget "microsoft.windowsappsdk.runtime\$runtime\tools\MSIX\win10-$arch\*.msix") $prerequisites
    Copy-Item (Join-Path $nuget "microsoft.ui.xaml\$winui\tools\AppX\$arch\Release\Microsoft.UI.Xaml.2.8.appx") $prerequisites

    # The MSI Around It
    dotnet build $installer -c Release `
        -p:Platform=$platform `
        -p:AppName=$name `
        -p:ProductVersion=$version `
        -p:PublishDir="$publish\\" `
        -p:PrerequisitesDir="$prerequisites\\" `
        -o $msiOut -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Building the MSI failed for $platform ($LASTEXITCODE)."
    }

    $msi = Join-Path $artifacts "$name-$version-$arch.msi"
    Copy-Item (Join-Path $msiOut "Messages.msi") $msi -Force
    Invoke-Sign $msi $certificate $signtool
    Write-Output $msi
}
