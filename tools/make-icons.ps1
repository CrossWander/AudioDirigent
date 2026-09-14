<#
    Рисует app.ico (рабочее состояние) и app-paused.ico (пауза).
    Знак — дирижёрская палочка: пробковая рукоять и сужающийся к острию стержень.
    Запускать вручную после правки рисунка: powershell -File tools\make-icons.ps1
#>
Add-Type -AssemblyName System.Drawing

$sizes = 256, 128, 64, 48, 32, 24, 16

# Плитка со скруглением: под неё пишет и Windows 11, и панель задач.
function Add-Tile([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$size) {
    $r = $size * 0.23
    $d = $r * 2
    $e = $size - 1
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc(($e - $d), 0, $d, $d, 270, 90)
    $path.AddArc(($e - $d), ($e - $d), $d, $d, 0, 90)
    $path.AddArc(0, ($e - $d), $d, $d, 90, 90)
    $path.CloseFigure()
}

function New-Bitmap([int]$size, [System.Drawing.Color]$from, [System.Drawing.Color]$to) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-Tile $tile $size
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF $size, $size), $from, $to)
    $g.FillPath($fill, $tile)

    # Палочка идёт из левого нижнего угла в правый верхний: по диагонали она читается
    # даже там, где на весь знак остаётся шестнадцать точек. На мелких размерах сужение
    # стержня пропадает в сглаживании и рукоять слипается с ним в одно пятно, поэтому
    # там стержень ровный и тонкий: точка и черта — всё, что успевает разглядеть глаз.
    $small = $size -le 32
    $gripX = $size * 0.29; $gripY = $size * 0.72
    $tipX  = $size * 0.77; $tipY  = $size * 0.25
    $grip  = $size * $(if ($small) { 0.145 } else { 0.115 })
    $wide  = $size * $(if ($small) { 0.062 } else { 0.075 })
    $thin  = $size * $(if ($small) { 0.062 } else { 0.024 })

    # Стержень — четырёхугольник: у рукояти он толще, у острия сходит на нет.
    $dx = $tipX - $gripX; $dy = $tipY - $gripY
    $len = [Math]::Sqrt($dx * $dx + $dy * $dy)
    $nx = -$dy / $len; $ny = $dx / $len

    $shaft = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shaft.AddPolygon(@(
        (New-Object System.Drawing.PointF (($gripX + $nx * $wide / 2), ($gripY + $ny * $wide / 2))),
        (New-Object System.Drawing.PointF (($tipX  + $nx * $thin / 2), ($tipY  + $ny * $thin / 2))),
        (New-Object System.Drawing.PointF (($tipX  - $nx * $thin / 2), ($tipY  - $ny * $thin / 2))),
        (New-Object System.Drawing.PointF (($gripX - $nx * $wide / 2), ($gripY - $ny * $wide / 2)))
    ))

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPath($white, $shaft)
    $g.FillEllipse($white, ($gripX - $grip), ($gripY - $grip), (2 * $grip), (2 * $grip))

    $white.Dispose(); $shaft.Dispose(); $fill.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

<#
    Кадр значка. PNG внутри .ico договорились понимать только у стороны 256; всё, что
    меньше, кладём точками — иначе GDI+ отказывается разбирать собственный файл.
#>
function New-Frame([System.Drawing.Bitmap]$bmp) {
    $size = $bmp.Width
    if ($size -ge 256) {
        $stream = New-Object System.IO.MemoryStream
        $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream

    $writer.Write([UInt32]40)                # BITMAPINFOHEADER
    $writer.Write([Int32]$size)
    $writer.Write([Int32]($size * 2))        # цвет и маска прозрачности одной высотой
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]32)
    $writer.Write([UInt32]0)                 # BI_RGB
    $writer.Write([UInt32]($size * $size * 4))
    $writer.Write([Int32]0); $writer.Write([Int32]0)
    $writer.Write([UInt32]0); $writer.Write([UInt32]0)

    # Точки лежат снизу вверх, по четыре байта: синий, зелёный, красный, прозрачность.
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($size * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy(
            [IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $writer.Write($row)
    }
    $bmp.UnlockBits($data)

    # Маска прозрачности не нужна — она в самих точках, но место под неё занять обязаны.
    $maskRow = New-Object byte[] ([Math]::Ceiling($size / 32.0) * 4)
    for ($y = 0; $y -lt $size; $y++) {
        $writer.Write($maskRow)
    }

    $writer.Flush()
    return ,$stream.ToArray()
}

function Save-Icon([string]$path, [System.Drawing.Color]$from, [System.Drawing.Color]$to) {
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($size in $sizes) {
        $bmp = New-Bitmap $size $from $to
        $frames.Add((New-Frame $bmp))
        $bmp.Dispose()
    }

    $file = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter $file

    $writer.Write([UInt16]0)                 # reserved
    $writer.Write([UInt16]1)                 # type: icon
    $writer.Write([UInt16]$sizes.Count)

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
        $writer.Write([byte]0)               # палитра не используется
        $writer.Write([byte]0)               # reserved
        $writer.Write([UInt16]1)             # color planes
        $writer.Write([UInt16]32)            # бит на пиксель
        $writer.Write([UInt32]$frames[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame)
    }

    $writer.Close(); $file.Close()
    Write-Host "$path - $($sizes.Count) sizes"
}

$root = Split-Path $PSScriptRoot -Parent
Save-Icon (Join-Path $root 'app.ico') `
    ([System.Drawing.Color]::FromArgb(0x4C, 0x6E, 0xF5)) ([System.Drawing.Color]::FromArgb(0x9B, 0x5B, 0xF2))
Save-Icon (Join-Path $root 'app-paused.ico') `
    ([System.Drawing.Color]::FromArgb(0x8A, 0x8A, 0x93)) ([System.Drawing.Color]::FromArgb(0x5E, 0x5E, 0x66))
