# Regenerates Icons/app.ico — the .exe's file icon (ApplicationIcon in the .csproj) and the
# MSI's ARPPRODUCTICON / Start Menu shortcut icon.
#
# The mark is the same record dot AppIcons.cs draws at runtime for the window and tray, just
# scaled: a ring inset 12.5% of the canvas, stroked at ~11%, around a filled 25% centre dot.
# Keeping one definition in two places is deliberate — the app must not depend on an icon file
# existing at runtime, and the installer cannot call into the app to draw one.
#
# Usage: pwsh Icons/generate-icon.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot 'app.ico'

# Sizes <= 64 are written as 32-bit BMP/DIB entries and the two large ones as PNG. Every
# shell surface on Windows 10/11 reads PNG-compressed entries, but a few older installer
# and property-sheet code paths still only look at the DIB ones, so the small sizes that
# those surfaces actually pick stay in the format they all understand.
$bmpSizes = 16, 20, 24, 32, 40, 48, 64
$pngSizes = 128, 256
$accent = [System.Drawing.Color]::FromArgb(214, 62, 62)

function New-Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $inset = $size * 0.125
        $stroke = [Math]::Max(1.0, $size * 0.109)
        # Inset by half the stroke as well: DrawEllipse centres the pen on the path, so without
        # it the outer edge of a thick ring is clipped by the canvas at small sizes.
        $ringInset = $inset + ($stroke / 2)
        $ringSize = $size - (2 * $ringInset)

        $pen = New-Object System.Drawing.Pen($accent, $stroke)
        $brush = New-Object System.Drawing.SolidBrush($accent)
        try {
            $g.DrawEllipse($pen, $ringInset, $ringInset, $ringSize, $ringSize)
            $dot = $size * 0.25
            $g.FillEllipse($brush, ($size - $dot) / 2, ($size - $dot) / 2, $dot, $dot)
        }
        finally { $pen.Dispose(); $brush.Dispose() }
    }
    finally { $g.Dispose() }
    return $bmp
}

function ConvertTo-Png($bitmap) {
    $ms = New-Object System.IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    # Leading comma: without it PowerShell unrolls the byte[] into the pipeline and the caller
    # gets an Object[] of boxed bytes, which BinaryWriter.Write binds to the wrong overload.
    return , $ms.ToArray()
}

function ConvertTo-Dib($bitmap) {
    $w = $bitmap.Width; $h = $bitmap.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $andStride = [int]([Math]::Floor(($w + 31) / 32)) * 4   # 1bpp mask rows pad to 4 bytes
    $xorBytes = $w * $h * 4

    # BITMAPINFOHEADER. Height is doubled because an icon DIB stores the colour (XOR) bitmap
    # and the 1bpp (AND) mask stacked in one image.
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($xorBytes + ($andStride * $h)))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $row = New-Object byte[] ($w * 4)
        for ($y = $h - 1; $y -ge 0; $y--) {   # DIB rows run bottom-up
            $src = [IntPtr]::Add($data.Scan0, $y * $data.Stride)
            [System.Runtime.InteropServices.Marshal]::Copy($src, $row, 0, $row.Length)
            $bw.Write($row)
        }
    }
    finally { $bitmap.UnlockBits($data) }

    # Fully-zero AND mask: the 32-bit alpha channel already carries transparency, and every
    # consumer that reads a 32bpp icon entry honours it.
    $bw.Write((New-Object byte[] ($andStride * $h)))
    $bw.Flush()
    return , $ms.ToArray()
}

$entries = @()
foreach ($size in $bmpSizes) {
    $bmp = New-Mark $size
    try { $entries += [pscustomobject]@{ Size = $size; Data = (ConvertTo-Dib $bmp) } }
    finally { $bmp.Dispose() }
}
foreach ($size in $pngSizes) {
    $bmp = New-Mark $size
    try { $entries += [pscustomobject]@{ Size = $size; Data = (ConvertTo-Png $bmp) } }
    finally { $bmp.Dispose() }
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$entries.Count)   # ICONDIR

    $offset = 6 + (16 * $entries.Count)
    foreach ($e in $entries) {
        # 0 in the width/height byte means 256 — the field is a single byte.
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([int16]1); $bw.Write([int16]32)
        $bw.Write([int]$e.Data.Length); $bw.Write([int]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Data) }
}
finally { $bw.Dispose(); $fs.Dispose() }

Write-Host "Wrote $out ($($entries.Count) sizes, $((Get-Item $out).Length) bytes)"
