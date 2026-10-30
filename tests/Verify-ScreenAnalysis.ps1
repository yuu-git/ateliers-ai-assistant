param([string]$Model = '')
$ErrorActionPreference = 'Stop'
$appPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'))
$reportPath = Join-Path $env:TEMP ('ateliers-screen-analysis-' + [Guid]::NewGuid().ToString('N') + '.txt')
$appArguments = @('--verify-screen-analysis', ('"' + $reportPath + '"'))
$workerProcess = $null
$testRoot = Join-Path $env:TEMP ('ateliers-screen-worker-' + [Guid]::NewGuid().ToString('N'))
if ($Model) {
    if ($Model -notmatch '^[A-Za-z0-9_.:/-]+$') { throw 'Invalid model name' }
    $appArguments += @($Model, ('"' + $testRoot + '"'))
}
$verificationProcess = $null
try {
    if ($Model) {
        $workerPath = Join-Path (Split-Path $appPath) 'Worker/Ateliers.Ai.Assistant.Worker.exe'
        $workerProcess = Start-Process -FilePath $workerPath -WorkingDirectory (Split-Path $workerPath) -WindowStyle Hidden -ArgumentList @('--verification-root', ('"' + $testRoot + '"')) -PassThru
        $endpointPath = Join-Path $testRoot 'l/Ateliers-Dev/Ai-Assistant.Debug/worker/endpoint.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path -LiteralPath $endpointPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath $endpointPath)) { throw 'Isolated Worker did not start' }
    }
    $verificationProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -ArgumentList $appArguments -PassThru
    if (!$verificationProcess.WaitForExit(300000)) { throw 'Screen analysis verification timed out' }
    if (!(Test-Path -LiteralPath $reportPath)) { throw 'Verification report missing' }
    $report = Get-Content -LiteralPath $reportPath -Raw
    Write-Output $report
    if ($report -match 'FAIL:' -or $report -notmatch 'PASS: ALL SCREEN ANALYSIS CHECKS') { throw ('Verification failed: ' + $reportPath) }
}
finally {
    foreach ($process in @($verificationProcess, $workerProcess)) {
        if ($null -ne $process) { $process.Refresh(); if (!$process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(10000) | Out-Null } }
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolved) -notlike 'ateliers-screen-worker-*') { throw 'Unexpected verification root' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
