param([switch]$Notifications, [switch]$GitHub)
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$testRoot = Join-Path $env:TEMP ('ateliers-worker-ui-' + [Guid]::NewGuid().ToString('N'))
$connectionPath = Join-Path $testRoot 'l/Ateliers-Dev/Ai-Assistant.Debug/worker/endpoint.json'
$workerIdentity = $null
function Run-Window([string]$mode) {
    $report = Join-Path $env:TEMP ('ateliers-worker-ui-report-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $uiProcess = Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -WindowStyle Hidden -PassThru -ArgumentList @('--verify-worker-ui', ('"' + $report + '"'), ('"' + $testRoot + '"'), $mode)
    try {
        if (!$uiProcess.WaitForExit(120000)) { throw ('UI verification timed out: ' + $mode) }
        if (!(Test-Path -LiteralPath $report)) { throw ('UI report missing: ' + $mode) }
        $text = Get-Content -LiteralPath $report -Raw
        Write-Output $text
        if ($text -match 'FAIL:' -or $text -notmatch 'PASS: ALL WORKER UI CHECKS') { throw ('Worker UI verification failed: ' + $report) }
    }
    finally { $uiProcess.Refresh(); if (!$uiProcess.HasExited) { Stop-Process -Id $uiProcess.Id -Force; $uiProcess.WaitForExit(10000) | Out-Null } }
}
function Worker-Status {
    return Invoke-RestMethod -Uri ($script:endpoint.address + 'api/v1/status') -Headers @{ Authorization = ('Bearer ' + $script:endpoint.token) } -TimeoutSec 5
}
try {
    if ($GitHub) { Run-Window 'github' } elseif ($Notifications) { Run-Window 'notifications' } else { Run-Window 'connected' }
    $workerIdentity = [int](Get-Content -LiteralPath (Join-Path $testRoot 'worker.pid') -Raw)
    $workerProcess = Get-Process -Id $workerIdentity
    $script:endpoint = Get-Content -LiteralPath $connectionPath -Raw | ConvertFrom-Json
    $initial = (Worker-Status).sources[0].lastFetchedAt
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do { Start-Sleep -Milliseconds 250; $advanced = (Worker-Status).sources[0].lastFetchedAt -ne $initial } while (!$advanced -and [DateTime]::UtcNow -lt $until)
    if (!$advanced) { throw 'Automatic polling did not continue after the actual Desktop process exited' }
    Write-Output 'PASS: actual Desktop process exited while its independently launched Worker remained alive and collected again'
    $null = Invoke-RestMethod -Uri ($endpoint.address + 'api/v1/shutdown') -Method Post -ContentType 'application/json' -Body '{}' -Headers @{ Authorization = ('Bearer ' + $endpoint.token) }
    if (!$workerProcess.WaitForExit(15000)) { throw 'Worker shutdown timeout' }
    Run-Window 'offline'
}
finally {
    $pidFile = Join-Path $testRoot 'worker.pid'
    if (Test-Path -LiteralPath $pidFile) {
        $workerIdentity = [int](Get-Content -LiteralPath $pidFile -Raw)
        $remaining = Get-Process -Id $workerIdentity -ErrorAction SilentlyContinue
        if ($null -ne $remaining) { Stop-Process -Id $workerIdentity -Force; $remaining.WaitForExit(10000) | Out-Null }
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolved) -notlike 'ateliers-worker-ui-*') { throw 'Unexpected test root' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
