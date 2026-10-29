param()
$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$webRoot = Join-Path $env:TEMP ('ateliers-web-ui-' + [Guid]::NewGuid().ToString('N'))
try {
    foreach ($stage in @('create', 'resume')) {
        $reportPath = Join-Path $env:TEMP ('ateliers-web-report-' + [Guid]::NewGuid().ToString('N') + '.txt')
        $arguments = @('--verify-web', ('"' + $reportPath + '"'), ('"' + $webRoot + '"'), $stage)
        $appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -ArgumentList $arguments -PassThru
        try {
            if (!$appProcess.WaitForExit(60000)) { throw ('Web UI check timed out: ' + $stage) }
            if (!(Test-Path -LiteralPath $reportPath)) { throw ('Web report missing: ' + $stage + ' exit=' + $appProcess.ExitCode) }
            $report = Get-Content -LiteralPath $reportPath -Raw
            Write-Output $report
            if ($report -match 'FAIL:' -or $report -notmatch 'PASS: ALL WEB DISPLAY CHECKS') { throw ('Web UI check failed: ' + $reportPath) }
        }
        finally {
            $appProcess.Refresh()
            if (!$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; if (!$appProcess.WaitForExit(8000)) { Stop-Process -Id $appProcess.Id } }
        }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($webRoot)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempRoot -or [IO.Path]::GetFileName($resolved) -notlike 'ateliers-web-ui-*') { throw 'Unexpected temporary root' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
