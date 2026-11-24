param(
    [string]$SourcePng = "assets\logo.png",
    [string]$OutputIco = "src\AutomatiskSportTV.App\Assets\app.ico"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sourceFull = (Resolve-Path $SourcePng).Path
$outDir = Split-Path -Parent $OutputIco
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

$sizes = @(256, 128, 64, 48, 32, 16)
$pngFrames = New-Object System.Collections.Generic.List[byte[]]

$srcImage = [System.Drawing.Image]::FromFile($sourceFull)
try {
    foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.DrawImage($srcImage, 0, 0, $size, $size)
            } finally {
                $g.Dispose()
            }

            $ms = New-Object System.IO.MemoryStream
            try {
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                $pngFrames.Add($ms.ToArray())
            } finally {
                $ms.Dispose()
            }
        } finally {
            $bmp.Dispose()
        }
    }
} finally {
    $srcImage.Dispose()
}

$fs = [System.IO.File]::Create($OutputIco)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    # ICONDIR header: reserved (0), type (1 = icon), count
    $bw.Write([UInt16]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $sz = $sizes[$i]
        $frame = $pngFrames[$i]
        $dimByte = if ($sz -ge 256) { [byte]0 } else { [byte]$sz }

        $bw.Write($dimByte)            # Width
        $bw.Write($dimByte)            # Height
        $bw.Write([byte]0)             # Color count
        $bw.Write([byte]0)             # Reserved
        $bw.Write([UInt16]1)           # Color planes
        $bw.Write([UInt16]32)          # Bits per pixel
        $bw.Write([UInt32]$frame.Length)
        $bw.Write([UInt32]$offset)
        $offset += $frame.Length
    }

    foreach ($frame in $pngFrames) {
        $bw.Write($frame)
    }
} finally {
    $bw.Dispose()
    $fs.Dispose()
}

Write-Host "Skapade Windows-ikon ($($sizes -join ', ') px): $OutputIco" -ForegroundColor Green
