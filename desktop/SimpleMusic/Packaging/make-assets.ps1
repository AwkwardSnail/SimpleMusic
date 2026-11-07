# Draws the SimpleMusic icon (a white half note on blue) at every size the
# app and the Microsoft Store need, plus the .ico used by SimpleMusic.exe.
# Run from Windows PowerShell:  .\make-assets.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'Assets'))

Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null

$blue  = [System.Windows.Media.SolidColorBrush][System.Windows.Media.Color]::FromRgb(0x2F, 0x5B, 0xD3)
$white = [System.Windows.Media.Brushes]::White

# The half note, designed in a 100 x 100 box: a slanted head with a slanted
# hollow centre, and a stem on the right.
function Get-NoteGeometry {
    $cx = 50; $cy = 68
    $outer = New-Object System.Windows.Media.EllipseGeometry((New-Object System.Windows.Point $cx, $cy), 15.5, 11)
    $outer.Transform = New-Object System.Windows.Media.RotateTransform(-22, $cx, $cy)
    $hole = New-Object System.Windows.Media.EllipseGeometry((New-Object System.Windows.Point $cx, $cy), 10.8, 3.9)
    $hole.Transform = New-Object System.Windows.Media.RotateTransform(-33, $cx, $cy)
    $head = [System.Windows.Media.Geometry]::Combine($outer, $hole, 'Exclude', $null)
    $stem = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect 59.2, 20, 5.8, 46))
    [System.Windows.Media.Geometry]::Combine($head, $stem, 'Union', $null)
}
$note = Get-NoteGeometry

# style 'tile' = rounded blue square (app icon, Store logo)
# style 'full' = blue edge to edge (Start tiles, splash)
function Save-Icon([string]$path, [int]$w, [int]$h, [string]$style, [double]$noteScale = 1.0) {
    $s = [Math]::Min($w, $h)
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    if ($style -eq 'tile') {
        $inset = [Math]::Max(0.5, $s * 0.03)
        $r = [System.Windows.Rect]::new(($w - $s) / 2 + $inset, ($h - $s) / 2 + $inset, $s - 2 * $inset, $s - 2 * $inset)
        $dc.DrawRoundedRectangle($blue, $null, $r, $s * 0.2, $s * 0.2)
    } else {
        $dc.DrawRectangle($blue, $null, [System.Windows.Rect]::new(0, 0, $w, $h))
    }
    $k = $s / 100 * $noteScale
    $m = [System.Windows.Media.Matrix]::new($k, 0, 0, $k, ($w - 100 * $k) / 2, ($h - 100 * $k) / 2)
    $dc.PushTransform([System.Windows.Media.MatrixTransform]::new($m))
    $dc.DrawGeometry($white, $null, $note)
    $dc.Pop()
    $dc.Close()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($w, $h, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [System.IO.File]::Create($path)
    $enc.Save($fs)
    $fs.Close()
}

# Store / Start / taskbar assets. The manifest names them without the
# .scale-xxx / .targetsize-xx part; Windows picks the best size.
foreach ($scale in 100, 200, 400) {
    $f = $scale / 100
    Save-Icon "$Out\Square44x44Logo.scale-$scale.png" (44 * $f) (44 * $f) 'tile'
    Save-Icon "$Out\StoreLogo.scale-$scale.png" (50 * $f) (50 * $f) 'tile'
    Save-Icon "$Out\Square150x150Logo.scale-$scale.png" (150 * $f) (150 * $f) 'full' 0.62
    Save-Icon "$Out\Wide310x150Logo.scale-$scale.png" (310 * $f) (150 * $f) 'full' 0.62
    Save-Icon "$Out\SmallTile.scale-$scale.png" (71 * $f) (71 * $f) 'full' 0.7
    Save-Icon "$Out\LargeTile.scale-$scale.png" (310 * $f) (310 * $f) 'full' 0.55
    Save-Icon "$Out\SplashScreen.scale-$scale.png" (620 * $f) (300 * $f) 'full' 0.5
}
# Tiny sizes get a slightly larger note so it stays readable.
function Get-SmallScale([int]$size) { if ($size -le 24) { 1.15 } elseif ($size -le 32) { 1.08 } else { 1.0 } }
foreach ($size in 16, 24, 32, 48, 256) {
    Save-Icon "$Out\Square44x44Logo.targetsize-$size.png" $size $size 'tile' (Get-SmallScale $size)
    Save-Icon "$Out\Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size 'tile' (Get-SmallScale $size)
}

# SimpleMusic.ico for the .exe and the window: PNG images inside an .ico container.
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $tmp = Join-Path $env:TEMP "sm-icon-$size.png"
    Save-Icon $tmp $size $size 'tile' (Get-SmallScale $size)
    , [System.IO.File]::ReadAllBytes($tmp)
    Remove-Item $tmp
}
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes("$Out\SimpleMusic.ico", $ms.ToArray())

Get-ChildItem $Out | Measure-Object | ForEach-Object { "Wrote $($_.Count) files to $Out" }
