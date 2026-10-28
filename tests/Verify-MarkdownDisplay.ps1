$ErrorActionPreference = 'Stop'
# Unpackaged / Debug / x64 ビルド後に実行。Ollama は起動・停止・呼び出ししない。
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$reportPath = Join-Path $env:TEMP ("ateliers-markdown-" + [Guid]::NewGuid().ToString('N') + '.txt')
$appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -ArgumentList @('--verify-markdown', ('"' + $reportPath + '"')) -PassThru
try {
    if (!$appProcess.WaitForExit(45000)) { throw 'Markdown display verification timed out' }
    if (!(Test-Path -LiteralPath $reportPath)) { throw "Verification report not created (exit $($appProcess.ExitCode))" }
    $report = Get-Content -LiteralPath $reportPath -Raw
    Write-Output $report.Substring(0, [Math]::Min($report.Length, 12000))
    if ($report -notmatch 'PASS: ALL MARKDOWN DISPLAY CHECKS') { throw "Markdown display verification failed. Report: $reportPath" }
}
finally {
    $appProcess.Refresh()
    if (!$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; if (!$appProcess.WaitForExit(2000)) { Stop-Process -Id $appProcess.Id } }
}
