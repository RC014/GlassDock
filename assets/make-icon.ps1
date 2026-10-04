# Draws the GlassDock logo with GDI+ and writes:
#   assets\GlassDock.ico   app/installer icon (16-256 px, PNG-compressed entries)
#   assets\logo.png        512 px logo for the README
# The logo: a blue-violet rounded square with a glass dock along the bottom holding three app tiles,
# the middle one raised and larger like the dock's hover wave.

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Logo([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.CompositingQuality = 'HighQuality'
    $g.Clear([Drawing.Color]::Transparent)
    $k = $size / 256.0
    $g.ScaleTransform($k, $k)

    # background: rounded square, blue to violet
    $bg = New-RoundRect 8 8 240 240 56
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 8, 8), (New-Object Drawing.PointF 248, 248), ([Drawing.Color]::FromArgb(255, 56, 132, 255)), ([Drawing.Color]::FromArgb(255, 139, 92, 246))
    $g.FillPath($grad, $bg)
    # soft light from the top
    $hl = New-RoundRect 8 8 240 120 56
    $hlBrush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 8), (New-Object Drawing.PointF 0, 128), ([Drawing.Color]::FromArgb(70, 255, 255, 255)), ([Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($hlBrush, $hl)

    # glass dock
    $dock = New-RoundRect 34 150 188 60 30
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(70, 255, 255, 255))), $dock)
    $sheen = New-RoundRect 36 152 184 28 26
    $sheenBrush = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, 150), (New-Object Drawing.PointF 0, 182), ([Drawing.Color]::FromArgb(80, 255, 255, 255)), ([Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($sheenBrush, $sheen)
    $rim = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(190, 255, 255, 255)), 3
    $g.DrawPath($rim, $dock)

    # three app tiles; the middle one is raised and larger (the hover wave)
    $tiles = @(
        @{ x = 52;  y = 160; s = 40; c1 = [Drawing.Color]::FromArgb(255, 255, 214, 74); c2 = [Drawing.Color]::FromArgb(255, 255, 160, 40) },
        @{ x = 104; y = 118; s = 48; c1 = [Drawing.Color]::FromArgb(255, 255, 255, 255); c2 = [Drawing.Color]::FromArgb(255, 214, 228, 255) },
        @{ x = 164; y = 160; s = 40; c1 = [Drawing.Color]::FromArgb(255, 74, 222, 128); c2 = [Drawing.Color]::FromArgb(255, 22, 163, 74) }
    )
    foreach ($t in $tiles) {
        $shadow = New-RoundRect ($t.x + 1) ($t.y + 4) $t.s $t.s ($t.s * 0.28)
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(60, 20, 20, 60))), $shadow)
        $tile = New-RoundRect $t.x $t.y $t.s $t.s ($t.s * 0.28)
        $tb = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.PointF 0, $t.y), (New-Object Drawing.PointF 0, ($t.y + $t.s)), $t.c1, $t.c2
        $g.FillPath($tb, $tile)
    }
    # running dot under the middle tile
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(230, 255, 255, 255))), 122, 196, 12, 6)

    $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray() # the comma keeps PowerShell from unrolling the byte array
}

# README logo
$logo = Draw-Logo 512
$logo.Save((Join-Path $dir 'logo.png'), [Drawing.Imaging.ImageFormat]::Png)

# Classic icon entry: 32-bit BGRA DIB (bottom-up, height doubled) followed by an all-zero AND mask.
# Smaller sizes must use this; many Windows APIs only understand PNG entries at 256 px.
function Get-DibBytes($bmp) {
    $s = $bmp.Width
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

# .ico: classic entries up to 128 px, PNG at 256 px
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $bmp = Draw-Logo $s
    if ($s -ge 256) { $images.Add((Get-PngBytes $bmp)) } else { $images.Add((Get-DibBytes $bmp)) }
}
$ms = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $ms
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $dir 'GlassDock.ico'), $ms.ToArray())
Write-Host "Wrote logo.png and GlassDock.ico"
