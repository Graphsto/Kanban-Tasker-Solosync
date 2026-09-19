$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $repo 'src/KanbanTasker.Desktop/Assets'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$source = [Drawing.Bitmap]::new((Join-Path $repo 'branding/kanban-tasker-win11-v3.png'))
try {
    # Trim transparent canvas padding only; preserve the approved artwork and alpha.
    $left = $source.Width; $top = $source.Height; $right = 0; $bottom = 0
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            if ($source.GetPixel($x, $y).A -ge 16) {
                $left = [Math]::Min($left, $x); $top = [Math]::Min($top, $y)
                $right = [Math]::Max($right, $x); $bottom = [Math]::Max($bottom, $y)
            }
        }
    }
    $crop = [Drawing.Rectangle]::new($left, $top, $right - $left + 1, $bottom - $top + 1)
    function Export-IconPng([int]$Size, [string]$FileName) {
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = ($Size * 0.92) / [Math]::Max($crop.Width, $crop.Height)
            $width = [single]($crop.Width * $scale); $height = [single]($crop.Height * $scale)
            $rect = [Drawing.RectangleF]::new(($Size - $width) / 2, ($Size - $height) / 2, $width, $height)
            $sourceRect = [Drawing.RectangleF]::new($crop.X, $crop.Y, $crop.Width, $crop.Height)
            $graphics.DrawImage($source, $rect, $sourceRect, [Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save((Join-Path $destination $FileName), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    foreach ($scalePercent in @(100,125,150,200,400)) {
        foreach ($asset in @(@('Square44x44Logo',44), @('Square150x150Logo',150), @('StoreLogo',50))) {
            Export-IconPng ([int][Math]::Ceiling($asset[1] * $scalePercent / 100)) "$($asset[0]).scale-$scalePercent.png"
        }
    }
    $sizes = @(16,24,32,48,64,128,256)
    foreach ($size in $sizes) {
        Export-IconPng $size "Square44x44Logo.targetsize-$size.png"
        foreach ($form in @('unplated','lightunplated')) {
            Copy-Item -LiteralPath (Join-Path $destination "Square44x44Logo.targetsize-$size.png") -Destination (Join-Path $destination "Square44x44Logo.targetsize-${size}_altform-$form.png") -Force
        }
    }
    Export-IconPng 256 'Logo.png'
    # ICO directory with PNG frames, used by both executable resources and window icons.
    $frames = @($sizes | ForEach-Object { ,[IO.File]::ReadAllBytes((Join-Path $destination "Square44x44Logo.targetsize-$_.png")) })
    $stream = [IO.File]::Create((Join-Path $destination 'KanbanTasker.ico'))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $source.Dispose() }
Write-Host "Exported approved logo variant 3 to $destination."
