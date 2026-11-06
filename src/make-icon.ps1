# Vẽ logo app (bộ não cách điệu trên nền bo góc xanh, kèm tia lấp lánh "dọn sạch") và ghi ra src\app.ico nhiều kích thước
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $f = $s / 256.0
    # Nền bo góc, gradient tím than -> xanh ngọc
    $r = 56 * $f; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $s - 1
    $path.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $path.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $path.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $path.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $s,$s), ([System.Drawing.Color]::FromArgb(58,42,160)), ([System.Drawing.Color]::FromArgb(0,180,170))
    $g.FillPath($bg, $path)
    # Bộ não nhìn từ trên: mỗi bán cầu ghép từ các hình tròn trắng chồng lên nhau
    $white = [System.Drawing.Brushes]::White
    foreach ($c in @(@(84,82,46),@(70,124,48),@(84,168,46),@(110,190,36),@(108,64,36),@(104,128,40),
                     @(172,82,46),@(186,124,48),@(172,168,46),@(146,190,36),@(148,64,36),@(152,128,40))) {
        $g.FillEllipse($white, ($c[0]-$c[2])*$f, ($c[1]-$c[2])*$f, 2*$c[2]*$f, 2*$c[2]*$f)
    }
    # Rãnh giữa và các nếp cuộn não (màu nền)
    $groove = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40,110,165)), ([single][Math]::Max(1.2, 9*$f))
    $groove.StartCap = 'Round'; $groove.EndCap = 'Round'
    $g.DrawLine($groove, 128*$f, 40*$f, 128*$f, 216*$f)
    if ($s -ge 32) {
        $groove.Width = [single][Math]::Max(1.2, 7*$f)
        foreach ($sign in -1, 1) {
            $x = { param($v) (128 + $sign * $v) * $f }
            $g.DrawBezier($groove, (& $x 26), (70*$f), (& $x 50), (60*$f), (& $x 60), (90*$f), (& $x 84), (84*$f))
            $g.DrawBezier($groove, (& $x 20), (128*$f), (& $x 44), (112*$f), (& $x 64), (144*$f), (& $x 92), (124*$f))
            $g.DrawBezier($groove, (& $x 28), (178*$f), (& $x 48), (162*$f), (& $x 62), (192*$f), (& $x 82), (172*$f))
        }
    }
    # Tia lấp lánh 4 cánh góc trên phải (bỏ ở cỡ nhỏ cho rõ hình)
    if ($s -ge 32) {
        $cx = 218*$f; $cy = 40*$f; $L = 28*$f; $t = 8*$f
        $pts = @((New-Object System.Drawing.PointF $cx,($cy-$L)), (New-Object System.Drawing.PointF ($cx+$t),($cy-$t)), (New-Object System.Drawing.PointF ($cx+$L),$cy), (New-Object System.Drawing.PointF ($cx+$t),($cy+$t)),
                 (New-Object System.Drawing.PointF $cx,($cy+$L)), (New-Object System.Drawing.PointF ($cx-$t),($cy+$t)), (New-Object System.Drawing.PointF ($cx-$L),$cy), (New-Object System.Drawing.PointF ($cx-$t),($cy-$t)))
        $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,214,90))), [System.Drawing.PointF[]]$pts)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $pngs += ,$ms.ToArray(); $bmp.Dispose()
}
# Ghi file .ico (ảnh PNG bên trong, hỗ trợ từ Windows Vista)
$out = Join-Path $PSScriptRoot 'app.ico'
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
# Ảnh xem trước
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'app-preview.png'), $pngs[-1])
