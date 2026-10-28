param()
$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$storageRoot = Join-Path $env:TEMP ('ateliers-storage-ui-' + [Guid]::NewGuid().ToString('N'))
foreach ($stage in @('create', 'resume')) {
    $reportPath = Join-Path $env:TEMP ('ateliers-storage-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $arguments = @('--verify-storage', ('"' + $reportPath + '"'), ('"' + $storageRoot + '"'), $stage)
    $appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -ArgumentList $arguments -PassThru
    try {
        if (!$appProcess.WaitForExit(90000)) { throw ('Storage UI check timed out: ' + $stage) }
        if (!(Test-Path -LiteralPath $reportPath)) { throw ('Storage report missing: ' + $stage + ' exit=' + $appProcess.ExitCode) }
        $report = Get-Content -LiteralPath $reportPath -Raw
        Write-Output $report
        if ($report -match 'FAIL:' -or $report -notmatch 'PASS: ALL STORAGE DISPLAY CHECKS') { throw ('Storage UI check failed: ' + $reportPath) }
    }
    finally {
        $appProcess.Refresh()
        if (!$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; if (!$appProcess.WaitForExit(8000)) { Stop-Process -Id $appProcess.Id } }
    }
}
# テストで生成した一意の一時ルートだけを削除する。ユーザーの AppData は使わない。
$resolvedStorageRoot = [IO.Path]::GetFullPath($storageRoot)
$resolvedTempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ([IO.Path]::GetDirectoryName($resolvedStorageRoot) -ne $resolvedTempRoot -or [IO.Path]::GetFileName($resolvedStorageRoot) -notlike 'ateliers-storage-ui-*') { throw 'Unexpected temporary storage root' }
if (Test-Path -LiteralPath $resolvedStorageRoot) { Remove-Item -LiteralPath $resolvedStorageRoot -Recurse -Force }
