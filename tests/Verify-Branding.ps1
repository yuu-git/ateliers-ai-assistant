param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [string]$DesktopDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$DesktopDirectory) { $DesktopDirectory = Join-Path $repo ('src/Ateliers.Ai.Assistant.Desktop/bin/x64/'+$Configuration+'/net8.0-windows10.0.19041.0/win-x64') }
if (![IO.Path]::IsPathRooted($DesktopDirectory)) {
    $DesktopDirectory = Join-Path (Get-Location).Path $DesktopDirectory
}
$DesktopDirectory = [IO.Path]::GetFullPath($DesktopDirectory)
$expectedVersion = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$sourceIcon = Join-Path $repo 'assets/branding/app.ico'
$sourceHash = (Get-FileHash -LiteralPath $sourceIcon -Algorithm SHA256).Hash
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BrandingNativeVerification {
    [DllImport("shell32.dll", EntryPoint="ExtractIconExW", CharSet=CharSet.Unicode)]
    public static extern uint ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll", EntryPoint="LoadImageW", CharSet=CharSet.Unicode)]
    public static extern IntPtr LoadImage(IntPtr instance, string path, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}
'@
function Assert-Check([bool]$ok, [string]$label) { if (!$ok) { throw $label }; Write-Output ('PASS: '+$label) }
function Assert-IconPixels([IntPtr]$actual, [string]$label) {
    $actualBitmap = ([Drawing.Icon]::FromHandle($actual)).ToBitmap()
    $expectedHandle = [BrandingNativeVerification]::LoadImage([IntPtr]::Zero, $sourceIcon, 1, $actualBitmap.Width, $actualBitmap.Height, 0x10)
    if ($expectedHandle -eq [IntPtr]::Zero) { throw 'Could not decode canonical ICO' }
    $expectedBitmap = ([Drawing.Icon]::FromHandle($expectedHandle)).ToBitmap()
    try {
        $different = 0
        for ($y=0; $y -lt $actualBitmap.Height; $y++) {
            for ($x=0; $x -lt $actualBitmap.Width; $x++) {
                $a=$actualBitmap.GetPixel($x,$y); $b=$expectedBitmap.GetPixel($x,$y)
                if ($a.A -ne $b.A -or ($a.A -ne 0 -and $a.ToArgb() -ne $b.ToArgb())) { $different++ }
            }
        }
        Assert-Check ($different -eq 0) ($label+' embedded icon pixels match the canonical ICO ('+$actualBitmap.Width+'px)')
    } finally { $actualBitmap.Dispose(); $expectedBitmap.Dispose(); [BrandingNativeVerification]::DestroyIcon($expectedHandle) | Out-Null }
}
$ico = [IO.File]::ReadAllBytes($sourceIcon)
$count = [BitConverter]::ToUInt16($ico,4)
Assert-Check ([BitConverter]::ToUInt16($ico,2) -eq 1 -and $count -ge 7) 'canonical ICO has a multi-size icon directory'
$sizes = @()
for ($entry=0; $entry -lt $count; $entry++) {
    $offset=6+16*$entry; $width=[int]$ico[$offset]; if ($width -eq 0) { $width=256 }; $sizes += $width
    $length=[BitConverter]::ToUInt32($ico,$offset+8); $start=[BitConverter]::ToUInt32($ico,$offset+12)
    Assert-Check ($start -ge 6+16*$count -and [long]$start+$length -le $ico.Length) ('ICO '+$width+'px entry is within the file')
}
foreach ($size in @(16,24,32,48,64,128,256)) { Assert-Check ($size -in $sizes) ('ICO includes '+$size+'px') }
foreach ($hostName in @('Desktop','Worker')) {
    $folder = if ($hostName -eq 'Worker') { Join-Path $DesktopDirectory 'Worker' } else { $DesktopDirectory }
    $runtimeIcon = Join-Path $folder 'Assets/Branding/app.ico'
    Assert-Check ((Get-FileHash -LiteralPath $runtimeIcon -Algorithm SHA256).Hash -eq $sourceHash) ($hostName+' runtime ICO is deployed unchanged')
    foreach ($extension in @('exe','dll')) {
        $binary = Join-Path $folder ('Ateliers.Ai.Assistant.'+$hostName+'.'+$extension)
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary)
        Assert-Check ($info.FileVersion -eq $expectedVersion+'.0' -and $info.ProductVersion.StartsWith($expectedVersion)) ($hostName+' '+$extension+' version '+$expectedVersion)
        Assert-Check ($info.ProductName -eq 'Ateliers AI Assistant' -and $info.CompanyName -eq 'yuuko') ($hostName+' '+$extension+' product and publisher metadata')
    }
    $large=[IntPtr]::Zero; $small=[IntPtr]::Zero
    $exe=Join-Path $folder ('Ateliers.Ai.Assistant.'+$hostName+'.exe')
    try {
        Assert-Check ([BrandingNativeVerification]::ExtractIconEx($exe,0,[ref]$large,[ref]$small,1) -gt 0) ($hostName+' EXE has an extractable Windows icon')
        Assert-IconPixels $large ($hostName+' large')
        Assert-IconPixels $small ($hostName+' small')
    } finally {
        if ($large -ne [IntPtr]::Zero) { [BrandingNativeVerification]::DestroyIcon($large) | Out-Null }
        if ($small -ne [IntPtr]::Zero) { [BrandingNativeVerification]::DestroyIcon($small) | Out-Null }
    }
}
$expectedSizes = @{
    'Square150x150Logo.scale-200.png'=@(300,300); 'Square44x44Logo.scale-200.png'=@(88,88)
    'Square44x44Logo.targetsize-24_altform-unplated.png'=@(24,24); 'StoreLogo.png'=@(50,50)
    'Wide310x150Logo.scale-200.png'=@(620,300); 'SplashScreen.scale-200.png'=@(1240,600); 'LockScreenLogo.scale-200.png'=@(48,48)
}
foreach ($name in $expectedSizes.Keys) {
    $png = [Drawing.Image]::FromFile((Join-Path $repo ('src/Ateliers.Ai.Assistant.Desktop/Assets/'+$name)))
    try { Assert-Check ($png.Width -eq $expectedSizes[$name][0] -and $png.Height -eq $expectedSizes[$name][1]) ('existing package asset dimensions: '+$name) } finally { $png.Dispose() }
}
foreach ($name in @('Package.appxmanifest','Package.Debug.appxmanifest')) {
    $manifest = [xml](Get-Content -LiteralPath (Join-Path $repo ('src/Ateliers.Ai.Assistant.Desktop/'+$name)) -Raw)
    Assert-Check ($manifest.Package.Identity.Version -eq $expectedVersion+'.0') ($name+' version matches the shared build version')
    Assert-Check ($manifest.Package.Identity.Publisher -eq 'CN=yuuko') ($name+' signing identity is preserved')
}
$win32 = [xml](Get-Content -LiteralPath (Join-Path $repo 'src/Ateliers.Ai.Assistant.Desktop/app.manifest') -Raw)
Assert-Check ($win32.assembly.assemblyIdentity.version -eq $expectedVersion+'.0') 'Win32 manifest version matches the shared build version'
Write-Output ('PASS: '+$Configuration+' branding verification complete: '+$DesktopDirectory)
