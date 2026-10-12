$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$svgPath = Join-Path $PSScriptRoot '..\icon.svg'
$assetsPath = Join-Path $PSScriptRoot '..\Assets'
New-Item -ItemType Directory -Path $assetsPath -Force | Out-Null

[xml]$svg = Get-Content -LiteralPath $svgPath -Raw
$viewBox = $svg.DocumentElement.GetAttribute('viewBox').Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
$viewWidth = [double]$viewBox[2]
$viewHeight = [double]$viewBox[3]
$rectangles = $svg.SelectNodes("//*[local-name()='rect']")

function Get-IconPng([int]$width, [int]$height) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $scale = [Math]::Min($width / $viewWidth, $height / $viewHeight)
        $offsetX = ($width - $viewWidth * $scale) / 2
        $offsetY = ($height - $viewHeight * $scale) / 2

        foreach ($rect in $rectangles) {
            $x = [double]$rect.GetAttribute('x')
            $y = [double]$rect.GetAttribute('y')
            $w = [double]$rect.GetAttribute('width')
            $h = [double]$rect.GetAttribute('height')
            $rx = [double]$rect.GetAttribute('rx')
            $ryText = $rect.GetAttribute('ry')
            $ry = if ($ryText) { [double]$ryText } else { $rx }
            $left = [single]($offsetX + $x * $scale)
            $top = [single]($offsetY + $y * $scale)
            $rectWidth = [single]($w * $scale)
            $rectHeight = [single]($h * $scale)
            $radiusX = [single]([Math]::Min($rx * $scale, $rectWidth / 2))
            $radiusY = [single]([Math]::Min($ry * $scale, $rectHeight / 2))
            $fill = [System.Drawing.ColorTranslator]::FromHtml($rect.GetAttribute('fill'))
            $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
            if ($radiusX -le 0 -or $radiusY -le 0) {
                $path.AddRectangle([System.Drawing.RectangleF]::new($left, $top, $rectWidth, $rectHeight))
            } else {
                $diameterX = 2 * $radiusX
                $diameterY = 2 * $radiusY
                $path.AddArc($left, $top, $diameterX, $diameterY, 180, 90)
                $path.AddArc($left + $rectWidth - $diameterX, $top, $diameterX, $diameterY, 270, 90)
                $path.AddArc($left + $rectWidth - $diameterX, $top + $rectHeight - $diameterY, $diameterX, $diameterY, 0, 90)
                $path.AddArc($left, $top + $rectHeight - $diameterY, $diameterX, $diameterY, 90, 90)
                $path.CloseFigure()
            }
            $brush = [System.Drawing.SolidBrush]::new($fill)
            try { $graphics.FillPath($brush, $path) }
            finally { $brush.Dispose(); $path.Dispose() }
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        } finally { $stream.Dispose() }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$png = Get-IconPng ([int]$viewWidth) ([int]$viewHeight)
[System.IO.File]::WriteAllBytes((Join-Path $assetsPath 'AutoPrompt.png'), $png)

$sizes = @(16, 32, 48, 256)
$images = foreach ($size in $sizes) { Get-IconPng $size $size }
$iconPath = Join-Path $assetsPath 'AutoPrompt.ico'
$iconStream = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $dimensionByte = if ($size -eq 256) { [byte]0 } else { [byte]$size }
        $writer.Write($dimensionByte)
        $writer.Write($dimensionByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }
    foreach ($image in $images) { $writer.Write([byte[]]$image) }
} finally {
    $writer.Dispose()
    $iconStream.Dispose()
}

Write-Output "Generated Assets/AutoPrompt.png and Assets/AutoPrompt.ico from icon.svg"
