param([string]$VisionModel = '')
$ErrorActionPreference = 'Stop'
# この明示的な検証だけがクリップボードを一時変更し、終了時に元の全形式を復元する。
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$reportPath = Join-Path $env:TEMP ("ateliers-image-paste-" + [Guid]::NewGuid().ToString('N') + '.txt')
$arguments = @('--verify-image-paste', ('"' + $reportPath + '"'))
if ($VisionModel) { $arguments += $VisionModel }
$appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -ArgumentList $arguments -PassThru
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    while (!$appProcess.WaitForExit(1000)) { if ($watch.Elapsed.TotalSeconds -gt 180) { throw 'Image paste verification timed out' } }
    if (!(Test-Path -LiteralPath $reportPath)) { throw "Report not created (exit $($appProcess.ExitCode))" }
    $report = Get-Content -LiteralPath $reportPath -Raw
    Write-Output $report.Substring(0, [Math]::Min($report.Length, 12000))
    if ($report -notmatch 'PASS: ALL IMAGE PASTE CHECKS' -or $report -match 'FAIL:') { throw "Image paste verification failed: $reportPath" }
}
finally {
    $appProcess.Refresh()
    if (!$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; if (!$appProcess.WaitForExit(8000)) { Stop-Process -Id $appProcess.Id } }
}
