<#
    Outputs into src/GMessages.App/Assets the notification Send button (Send.scale-*.png): the Segoe Fluent Icons filled
    Send glyph for the toast's quick reply button. The app icon, logos and tray icons come from scripts/Make-Icons.ps1.

    Rendering uses WPF, so run it from an STA PowerShell:

        pwsh -STA -File .\scripts\New-AppAssets.ps1

    Uses WPF drawing (https://learn.microsoft.com/dotnet/desktop/wpf/graphics-multimedia/drawing-objects-overview)
    and System.Drawing (https://learn.microsoft.com/dotnet/api/system.drawing) for saving.
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\GMessages.App\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# Renders a WPF visual into a System.Drawing bitmap (for PNG saving)
function ConvertTo-Bitmap {
    param(
        [System.Windows.Media.Visual] $Visual,
        [int] $Size
    )

    $target = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($Visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($target))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    $stream.Position = 0
    return , (New-Object System.Drawing.Bitmap $stream)
}

# Notification Send Button: the Segoe Fluent Icons filled Send glyph (E725), for the toast's quick reply button.
# Microsoft's quick reply guidance asks for a 32 px icon at 100% scale
# (https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-content#quick-reply-text-box),
# but Windows stretches the image over the whole button, so a glyph filling it looks oversized and blurry. The glyph is
# drawn at half the canvas, centered on its ink, with one file per display scale so Windows never stretches a smaller
# one: the packaged app names Send.png and Windows picks the Send.scale-*.png that fits. Toasts follow the system theme,
# so it is white, as the guidance asks (white pixels on transparent)
Get-ChildItem -Path $assets -Filter 'Send*.png' | Remove-Item
foreach ($scale in 100, 125, 150, 200, 400) {
    $sendCanvas = [int](32 * $scale / 100)
    $sendSize   = [int](16 * $scale / 100)
    $sendText   = New-Object System.Windows.Media.FormattedText (
        [string][char]0xE725,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight,
        (New-Object System.Windows.Media.Typeface 'Segoe Fluent Icons'),
        $sendSize,
        (New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Colors]::White)),
        1.0)
    $sendInk     = $sendText.BuildGeometry((New-Object System.Windows.Point 0, 0)).Bounds
    $sendVisual  = New-Object System.Windows.Media.DrawingVisual
    $sendContext = $sendVisual.RenderOpen()
    $sendContext.DrawText($sendText, (New-Object System.Windows.Point (($sendCanvas - $sendInk.Width) / 2 - $sendInk.X), (($sendCanvas - $sendInk.Height) / 2 - $sendInk.Y)))
    $sendContext.Close()
    $sendBitmap = ConvertTo-Bitmap -Visual $sendVisual -Size $sendCanvas
    $sendBitmap.Save((Join-Path $assets "Send.scale-$scale.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $sendBitmap.Dispose()
    Write-Output "Wrote $(Join-Path $assets "Send.scale-$scale.png")"
}
