<#
    Renders Kaituro's icons into src/GMessages.App/Assets (see scripts/make-icons.cjs): the MSIX logos at every scale and
    target size, the window/exe icon (AppIcon.ico) from assets-src/Kaituro.svg, and the tray icons (TrayLight.ico,
    TrayDark.ico) from assets-src/Kaituro-mono.svg. The notification Send button comes from scripts/New-AppAssets.ps1.

    Ported from Leaf Calendar's tools/make-icons.ps1. Uses resvg (https://github.com/yisibl/resvg-js), installed by npm
    into a temp folder; needs Node.js, no admin.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tool = Join-Path ([IO.Path]::GetTempPath()) 'kaituro-make-icons'

# Renderer (npm, outside the repo)
New-Item -ItemType Directory -Force $tool | Out-Null
$resvg     = '2.6.2'
$installed = Join-Path $tool 'node_modules/@resvg/resvg-js/package.json'
if (-not (Test-Path $installed) -or (Get-Content $installed -Raw | ConvertFrom-Json).version -ne $resvg) {
    npm install --prefix $tool --no-save --silent --ignore-scripts "@resvg/resvg-js@$resvg" | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Render (from the tool folder, where the renderer resolves)
Push-Location $tool
try {
    node (Join-Path $PSScriptRoot 'make-icons.cjs') (Join-Path $root 'assets-src') (Join-Path $root 'src/GMessages.App/Assets')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

Write-Host 'Icons written to src/GMessages.App/Assets'
