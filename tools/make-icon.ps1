# Generates src/ScreenRecorder.App/Assets/app.ico (blue tile, white camera, red record dot).
# Run with Windows PowerShell:  powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 7 * $s; $d = 2 * $r; $x = 1 * $s; $y = 1 * $s; $w = 30 * $s
    $path.AddArc($x, $y, $d, $d, 180, 90); $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90); $path.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(47, 127, 224))), $path)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillRectangle($white, 6 * $s, 9 * $s, 14 * $s, 14 * $s)
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (20 * $s), (14 * $s)), (New-Object System.Drawing.PointF (26 * $s), (10 * $s)),
        (New-Object System.Drawing.PointF (26 * $s), (22 * $s)), (New-Object System.Drawing.PointF (20 * $s), (18 * $s)))
    $g.FillPolygon($white, $pts)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(224, 38, 42))), 9 * $s, 12 * $s, 8 * $s, 8 * $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = @(); foreach ($sz in $sizes) { $images += ,(New-IconPng $sz) }
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
$target = Join-Path $PSScriptRoot "..\src\ScreenRecorder.App\Assets\app.ico"
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Wrote $target ($($out.Length) bytes)"
