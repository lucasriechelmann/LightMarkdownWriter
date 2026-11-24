param([string]$Out, [string]$PreviewOut)
# Multi-size .ico (PNG entries): "MD" in white on a dark tile, with a Windows-blue baseline bar.
Add-Type -AssemblyName System.Drawing
$sizes = 256, 64, 48, 32, 24, 16

function New-Tile([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.Clear([System.Drawing.Color]::Transparent)

    # Dark rounded tile with a faint top-light gradient and a hairline edge so it reads on dark taskbars too.
    $pad = [Math]::Max(0.5, $s * 0.04); $r = [Math]::Max(3, $s * 0.24)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90); $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90); $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90); $path.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(0x3A, 0x40, 0x4A)), ([System.Drawing.Color]::FromArgb(0x1C, 0x1F, 0x24)), 90
    $g.FillPath($fill, $path)
    if ($s -ge 24) {
        $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([Math]::Max(1, $s / 128))
        $g.DrawPath($edge, $path)
    }

    # "MD": bold, tight; sized so both letters fit with margin at every size.
    $fontSize = if ($s -le 16) { $s * 0.56 } elseif ($s -le 24) { $s * 0.5 } else { $s * 0.42 }
    $font = New-Object System.Drawing.Font 'Segoe UI', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = [System.Drawing.StringFormat]::GenericTypographic; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $textY = if ($s -ge 32) { -$s * 0.05 } else { -$s * 0.02 }
    $g.DrawString('MD', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, $textY, $s, $s), $fmt)

    # Accent baseline (Windows 11 blue), like a cursor/underline — only where there's room.
    if ($s -ge 24) {
        $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x47, 0x9E, 0xF5))
        $w = $s * 0.46; $h = [Math]::Max(2, $s * 0.055)
        $g.FillRectangle($bar, ($s - $w) / 2, $s * 0.72, $w, $h)
    }
    $g.Dispose()
    return $bmp
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Tile $s
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()

# Preview sheet: each size at 1:1 on a light and a dark strip (how it looks in Explorer/taskbar either theme).
if ($PreviewOut) {
    $sheet = New-Object System.Drawing.Bitmap 460, 300
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))), 0, 0, 460, 150)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 0, 150, 460, 150)
    foreach ($y in 10, 160) {
        $x = 10
        foreach ($s in 128, 64, 48, 32, 24, 16) {
            $ico = New-Object System.Drawing.Icon $Out, $s, $s
            $g.DrawImage($ico.ToBitmap(), $x, $y + (128 - $s) / 2, $s, $s); $x += $s + 14
        }
    }
    $sheet.Save($PreviewOut)
}
"wrote $Out ($((Get-Item $Out).Length) bytes)"
