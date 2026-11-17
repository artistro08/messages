<#
    Installs the Windows packages Messages needs, for the person running the setup: the Windows App SDK runtime
    (notifications, single instance, power events) and the WinUI 2.8 framework (the app's controls).

    Run by the MSI after it copies the files. The packages sit beside this script; Build-Installer.ps1 copies them from
    the Microsoft.WindowsAppSDK.Runtime and Microsoft.UI.Xaml NuGet packages. A package that's already installed, in
    the same or a newer version, is left alone. Depends on Add-AppxPackage, part of Windows:
    https://learn.microsoft.com/powershell/module/appx/add-appxpackage
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# The Name, Version And Architecture A Package File Declares (its AppxManifest.xml)
function Get-PackageIdentity([string] $Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $reader = New-Object IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
        $identity = ([xml]$reader.ReadToEnd()).Package.Identity
        $reader.Dispose()
        return $identity
    }
    finally {
        $zip.Dispose()
    }
}

# Frameworks First: Main, Singleton and DDLM depend on the runtime framework
$packages = Get-ChildItem $PSScriptRoot -File | Where-Object { $_.Extension -in '.msix', '.appx' } |
    Sort-Object { $_.Name -match '\.(Main|Singleton|DDLM)\.' }, Name

foreach ($package in $packages) {
    # Skip It When This Version, Or A Newer One, Is Already Installed
    $identity  = Get-PackageIdentity $package.FullName
    $installed = Get-AppxPackage -Name $identity.Name |
        Where-Object { $_.Architecture.ToString() -eq $identity.ProcessorArchitecture -and [version]$_.Version -ge [version]$identity.Version }
    if ($installed) {
        continue
    }

    try {
        Add-AppxPackage -Path $package.FullName
    }
    catch {
        Write-Error "Installing $($package.Name) failed: $($_.Exception.Message)"
        exit 1
    }
}
