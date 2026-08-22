param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [single]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Bounds.Right - $diameter, $Bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Bounds.Left, $Bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Convert-Point {
    param([double]$X, [double]$Y, [double]$Scale)
    return [System.Drawing.PointF]::new([single]($X * $Scale), [single]($Y * $Scale))
}

function New-LogoBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 100.0
        $margin = [single](3 * $scale)
        $backgroundBounds = [System.Drawing.RectangleF]::new(
            $margin,
            $margin,
            [single]($Size - (2 * $margin)),
            [single]($Size - (2 * $margin)))
        $backgroundPath = New-RoundedPath -Bounds $backgroundBounds -Radius ([single](20 * $scale))
        try {
            $backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
                $backgroundBounds,
                [System.Drawing.Color]::FromArgb(255, 43, 104, 224),
                [System.Drawing.Color]::FromArgb(255, 36, 166, 226),
                45.0)
            try {
                $graphics.FillPath($backgroundBrush, $backgroundPath)
            }
            finally {
                $backgroundBrush.Dispose()
            }

            $glowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 255, 255, 255))
            try {
                $graphics.SetClip($backgroundPath)
                $graphics.FillEllipse(
                    $glowBrush,
                    [single](-12 * $scale),
                    [single](-18 * $scale),
                    [single](88 * $scale),
                    [single](72 * $scale))
                $graphics.ResetClip()
            }
            finally {
                $graphics.ResetClip()
                $glowBrush.Dispose()
            }

            $borderPen = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(72, 255, 255, 255),
                [single]([Math]::Max(1, 1.5 * $scale)))
            try {
                $graphics.DrawPath($borderPen, $backgroundPath)
            }
            finally {
                $borderPen.Dispose()
            }
        }
        finally {
            $backgroundPath.Dispose()
        }

        $leftTop = Convert-Point 15 44 $scale
        $tip = Convert-Point 85 15 $scale
        $center = Convert-Point 36 54 $scale
        $tail = Convert-Point 27 74 $scale
        $fold = Convert-Point 46 60 $scale
        $bottom = Convert-Point 61 85 $scale

        $planeOutline = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $planeOutline.AddPolygon([System.Drawing.PointF[]]@($leftTop, $tip, $bottom, $fold, $tail, $center))
            $baseBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 211, 235, 255))
            try {
                $graphics.FillPath($baseBrush, $planeOutline)
            }
            finally {
                $baseBrush.Dispose()
            }

            $upperBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 250, 253, 255))
            try {
                $graphics.FillPolygon($upperBrush, [System.Drawing.PointF[]]@($leftTop, $tip, $center))
            }
            finally {
                $upperBrush.Dispose()
            }

            $mainWingBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 183, 222, 255))
            try {
                $graphics.FillPolygon($mainWingBrush, [System.Drawing.PointF[]]@($center, $tip, $bottom, $fold))
            }
            finally {
                $mainWingBrush.Dispose()
            }

            $tailBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 112, 190, 246))
            try {
                $graphics.FillPolygon($tailBrush, [System.Drawing.PointF[]]@($center, $fold, $tail))
            }
            finally {
                $tailBrush.Dispose()
            }

            $outlinePen = [System.Drawing.Pen]::new(
                [System.Drawing.Color]::FromArgb(170, 17, 66, 151),
                [single]([Math]::Max(1, 1.5 * $scale)))
            try {
                $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                $outlinePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $outlinePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $graphics.DrawPath($outlinePen, $planeOutline)
                $graphics.DrawLine($outlinePen, $center, $tip)
                $graphics.DrawLine($outlinePen, $center, $fold)
            }
            finally {
                $outlinePen.Dispose()
            }
        }
        finally {
            $planeOutline.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

$pngPath = Join-Path $PSScriptRoot 'wfly-logo-blue.png'
$icoPath = Join-Path $PSScriptRoot 'wfly.ico'

$large = New-LogoBitmap -Size 512
try {
    $large.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $large.Dispose()
}

$entries = [System.Collections.Generic.List[object]]::new()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = New-LogoBitmap -Size $size
    $memory = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $entries.Add([pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() })
    }
    finally {
        $memory.Dispose()
        $bitmap.Dispose()
    }
}

$file = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    $offset = 6 + (16 * $entries.Count)
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $entries) {
        $writer.Write([byte[]]$entry.Bytes)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output "Generated $pngPath and $icoPath"
