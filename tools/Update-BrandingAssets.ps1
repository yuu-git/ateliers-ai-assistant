param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repo 'assets/branding/icon-1024.png'
$target = Join-Path $repo 'src/Ateliers.Ai.Assistant.Desktop/Assets'
$master = [Drawing.Image]::FromFile($source)
function Write-BrandImage([string]$name, [int]$width, [int]$height, [int]$iconSize) {
    $bitmap = [Drawing.Bitmap]::new($width, $height, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($master, [Drawing.Rectangle]::new([int](($width-$iconSize)/2), [int](($height-$iconSize)/2), $iconSize, $iconSize))
        $bitmap.Save((Join-Path $target $name), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
try {
    Write-BrandImage 'Square150x150Logo.scale-200.png' 300 300 300
    Write-BrandImage 'Square44x44Logo.scale-200.png' 88 88 88
    Write-BrandImage 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24 24
    Write-BrandImage 'StoreLogo.png' 50 50 50
    Write-BrandImage 'Wide310x150Logo.scale-200.png' 620 300 256
    Write-BrandImage 'SplashScreen.scale-200.png' 1240 600 300
    Write-BrandImage 'LockScreenLogo.scale-200.png' 48 48 48
} finally { $master.Dispose() }
Write-Output 'Updated existing package image assets from assets/branding/icon-1024.png.'
