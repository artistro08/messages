<#
    Makes src/GMessages.App/Assets/Fonts/FluentEmojiColor.zip, the Fluent emoji font the app draws emoji with (see the
    ContentControlThemeFontFamily note in src/GMessages.App/App.xaml), and the two MIT license files beside it.

    Windows' own text engine (the one WinUI 2.8 uses) draws the font's SVG and bitmap (CBDT) glyphs but not its COLR v1
    ones. The bitmaps are kept: an SVG emoji took about 150 ms to draw the first time (a message full of new ones froze
    the app), a bitmap one about 12 ms. They come at 109 px and are scaled down to -Ppem (48: sharp for the picker's 20 px
    emoji on a 200% display), so the zip is about 13.5 MB, not 32. The build unzips it (GMessages.App.csproj).

    The font is Fluent Emoji Color from tetunori/fluent-emoji-webfont (https://github.com/tetunori/fluent-emoji-webfont),
    drawn from Microsoft's fluentui-emoji (https://github.com/microsoft/fluentui-emoji). Uses fontTools
    (https://github.com/fonttools/fonttools) and Pillow (https://python-pillow.org), installed by pip into a temp
    folder; needs Python 3, no admin.
#>

param(
    # The Bitmaps' Size In Pixels Per Em
    [int] $Ppem = 48
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path $PSScriptRoot -Parent
$fonts  = Join-Path $root 'src/GMessages.App/Assets/Fonts'
$work   = Join-Path ([IO.Path]::GetTempPath()) 'messages-make-emoji-font'
$tools  = Join-Path $work 'fonttools'
$commit = 'b7fc5ad4ceb3c7d665087040073d4b64490af96f'
$source = "https://raw.githubusercontent.com/tetunori/fluent-emoji-webfont/$commit"

# Download The Font And Both Licenses
New-Item -ItemType Directory -Force $work, $fonts | Out-Null
$full = Join-Path $work 'FluentEmojiColor-full.ttf'
if (-not (Test-Path $full)) {
    Invoke-WebRequest "$source/dist/FluentEmojiColor.ttf" -OutFile $full
}
Invoke-WebRequest "$source/LICENSE" -OutFile (Join-Path $fonts 'LICENSE-fluent-emoji-webfont.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/microsoft/fluentui-emoji/main/LICENSE' -OutFile (Join-Path $fonts 'LICENSE-fluentui-emoji.txt')

# fontTools And Pillow (pip, Outside The Repo)
if (-not (Test-Path (Join-Path $tools 'PIL'))) {
    python -m pip install --quiet --target $tools fonttools pillow | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Keep Only The Bitmap Glyphs, Scaled To -Ppem
$trimmed = Join-Path $work 'FluentEmojiColor.ttf'
$script  = Join-Path $work 'trim.py'
@'
import io, sys
sys.path.insert(0, sys.argv[1])
from fontTools.ttLib import TTFont
from PIL import Image

font = TTFont(sys.argv[2])
ppem = int(sys.argv[4])
for table in ('SVG ', 'COLR', 'CPAL'):
    if table in font:
        del font[table]

# Ordinary Text Left To The Text Font: The App Lists This Font First (So Joined Emoji Stay In It), And These Would
# Otherwise Draw From It (Its Space Is As Wide As An Emoji). Keycap Bases Go Too; A Keycap Shows As Its Digit Then
for subtable in font['cmap'].tables:
    for code in [0x20, 0x23, 0x2A, 0xA9, 0xAE, *range(0x30, 0x3A)]:
        subtable.cmap.pop(code, None)

# Segoe UI Variable's Line Height And Baseline (2048 Units To The Em There, This Font's Own Here): A Text Box Takes
# Them From The First Font Listed, And This One's Shorter Line Put The Message Box's Text Too High
upm = font['head'].unitsPerEm
hhea, os2 = font['hhea'], font['OS/2']
hhea.ascent, hhea.descent, hhea.lineGap = round(2210 * upm / 2048), -round(514 * upm / 2048), 0
os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap = round(1491 * upm / 2048), -round(431 * upm / 2048), round(269 * upm / 2048)
os2.usWinAscent, os2.usWinDescent = round(2210 * upm / 2048), round(514 * upm / 2048)
os2.fsSelection &= ~(1 << 7)

# No Missing-Glyph Box: A Joined Emoji The Font Lacks (A Gendered Family) Drew It At Each Join
from fontTools.ttLib.tables._g_l_y_f import Glyph
font['glyf']['.notdef'] = Glyph()

# Strike: Its Size And Line Metrics
strike = font['CBLC'].strikes[0].bitmapSizeTable
scale  = ppem / strike.ppemX
strike.ppemX = strike.ppemY = ppem
for line in (strike.hori, strike.vert):
    for name in ('ascender', 'descender', 'widthMax', 'minOriginSB', 'minAdvanceSB', 'maxBeforeBL', 'minAfterBL'):
        setattr(line, name, round(getattr(line, name) * scale))

# Glyphs: Each PNG And Its Metrics
for glyph in font['CBDT'].strikeData[0].values():
    glyph.ensureDecompiled()
    image  = Image.open(io.BytesIO(glyph.imageData))
    width  = max(1, round(image.width * scale))
    height = max(1, round(image.height * scale))
    out    = io.BytesIO()
    image.resize((width, height), Image.LANCZOS).save(out, 'PNG', optimize=True)
    glyph.imageData = out.getvalue()
    m = glyph.metrics
    m.width, m.height = width, height
    m.BearingX, m.BearingY, m.Advance = round(m.BearingX * scale), round(m.BearingY * scale), round(m.Advance * scale)

font.save(sys.argv[3])
'@ | Set-Content $script
python -I $script $tools $full $trimmed $Ppem
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Zip For The Repository
$zip = Join-Path $fonts 'FluentEmojiColor.zip'
Compress-Archive -Path $trimmed -DestinationPath $zip -CompressionLevel Optimal -Force
Write-Output "$zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
