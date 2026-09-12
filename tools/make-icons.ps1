<#
    Рисует app.ico (рабочее состояние) и app-paused.ico (пауза).
    Запускать вручную после правки рисунка: powershell -File tools\make-icons.ps1
#>
Add-Type -AssemblyName System.Drawing

$sizes = 256, 128, 64, 48, 32, 24, 16

function New-Frame([int]$size, [System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $brush = New-Object System.Drawing.SolidBrush $color
    $g.FillEllipse($brush, 0, 0, $size - 1, $size - 1)

    # Оголовье и чашки наушников
    $stroke = [Math]::Max(1.6, $size * 0.085)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $stroke
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $radius = $size * 0.27
    $cx = $size / 2.0
    $cy = $size * 0.50
    $g.DrawArc($pen, ($cx - $radius), ($cy - $radius), (2 * $radius), (2 * $radius), 180, 180)
    $g.DrawLine($pen, ($cx - $radius), $cy, ($cx - $radius), ($cy + $size * 0.16))
    $g.DrawLine($pen, ($cx + $radius), $cy, ($cx + $radius), ($cy + $size * 0.16))

    $pen.Dispose(); $brush.Dispose(); $g.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$stream.ToArray()
}

function Save-Icon([string]$path, [System.Drawing.Color]$color) {
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($size in $sizes) {
        $frames.Add((New-Frame $size $color))
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
    Write-Host "$path — $($sizes.Count) размеров"
}

$root = Split-Path $PSScriptRoot -Parent
Save-Icon (Join-Path $root 'app.ico') ([System.Drawing.Color]::FromArgb(0xE5, 0x35, 0x2E))
Save-Icon (Join-Path $root 'app-paused.ico') ([System.Drawing.Color]::FromArgb(0x60, 0x66, 0x74))
