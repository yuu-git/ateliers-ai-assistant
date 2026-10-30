param()
$ErrorActionPreference = 'Stop'
$workerExe = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Worker/Ateliers.Ai.Assistant.Worker.exe'
$testRoot = Join-Path $env:TEMP ('ateliers-worker-process-' + [Guid]::NewGuid().ToString('N'))
$connectionPath = Join-Path $testRoot 'l/Ateliers-Dev/Ai-Assistant.Debug/worker/endpoint.json'
$script:workerProcess = $null
$script:endpoint = $null
function Wait-For([scriptblock]$check, [string]$description) {
    $until = [DateTime]::UtcNow.AddSeconds(60)
    do { if (& $check) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $until)
    throw ('Timed out: ' + $description)
}
function Api([string]$method, [string]$route, $body = $null) {
    $request = @{ Uri = ($endpoint.address + 'api/v1/' + $route); Method = $method; Headers = @{ Authorization = ('Bearer ' + $endpoint.token) }; TimeoutSec = 10 }
    if ($null -ne $body) { $request.ContentType = 'application/json'; $request.Body = ($body | ConvertTo-Json -Depth 20 -Compress) }
    $response = Invoke-RestMethod @request
    if ($route -in @('runs', 'notifications')) { foreach ($entry in $response) { Write-Output $entry }; return }
    return $response
}
function Start-Worker([string]$previous = '') {
    $script:workerProcess = Start-Process -FilePath $workerExe -WorkingDirectory (Split-Path $workerExe) -WindowStyle Hidden -PassThru -ArgumentList @('--verification-root', ('"' + $testRoot + '"'))
    Wait-For {
        if (!(Test-Path -LiteralPath $connectionPath)) { return $false }
        try {
            $script:endpoint = Get-Content -LiteralPath $connectionPath -Raw | ConvertFrom-Json
            if ($endpoint.instanceId -eq $previous) { return $false }
            $null = Api GET status
            return $true
        } catch { return $false }
    } 'Worker API ready'
}
function Save-Config($config) { return Api PUT configuration $config }
function Expect-Status([int]$status, [hashtable]$parameters) {
    try { Invoke-WebRequest @parameters | Out-Null } catch { if ([int]$_.Exception.Response.StatusCode -eq $status) { return }; throw }
    throw ('Expected HTTP ' + $status)
}
try {
    $orphanOwner = Join-Path $testRoot ('l/Ateliers-Dev/Ai-Assistant.Debug/temp/v1/' + [Guid]::NewGuid().ToString('N'))
    $orphanWork = Join-Path $orphanOwner 'work'
    [IO.Directory]::CreateDirectory($orphanWork) | Out-Null
    $orphanPayload = Join-Path $orphanWork ([Guid]::NewGuid().ToString('N') + '.part')
    [IO.File]::WriteAllBytes($orphanPayload, [byte[]]@(1, 2, 3))
    [IO.File]::WriteAllText((Join-Path $orphanOwner 'owner.lock'), '')
    Start-Worker
    if (Test-Path -LiteralPath $orphanPayload) { throw 'Worker startup did not reclaim abandoned Temp' }
    Write-Output 'PASS: actual Worker startup reclaims abandoned managed Temp before publishing API'
    $initial = Api GET status
    if ($initial.configuration.allowLoad -or $initial.configuration.allowUnload -or $initial.configuration.aiPaused) { throw 'Unsafe default resource policy' }
    $headers = @{ Authorization = ('Bearer ' + $endpoint.token) }
    Expect-Status 401 @{ Uri = ($endpoint.address + 'api/v1/status'); TimeoutSec = 5 }
    Expect-Status 403 @{ Uri = ($endpoint.address + 'api/v1/status'); Headers = @{ Authorization = ('Bearer ' + $endpoint.token); Origin = 'https://example.com' }; TimeoutSec = 5 }
    Expect-Status 403 @{ Uri = ($endpoint.address + 'api/v1/status'); Headers = @{ Authorization = ('Bearer ' + $endpoint.token); Host = 'evil.example' }; TimeoutSec = 5 }
    Expect-Status 415 @{ Uri = ($endpoint.address + 'api/v1/fetch'); Method = 'Post'; Headers = $headers; ContentType = 'text/plain'; Body = '{}'; TimeoutSec = 5 }
    $port = ([Uri]$endpoint.address).Port
    $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | Where-Object { $_.Port -eq $port }
    if (!$listeners -or @($listeners | Where-Object { $_.Address.ToString() -ne '127.0.0.1' }).Count -ne 0) { throw 'API listener is not restricted to IPv4 loopback' }
    $duplicate = Start-Process -FilePath $workerExe -WorkingDirectory (Split-Path $workerExe) -WindowStyle Hidden -PassThru -ArgumentList @('--verification-root', ('"' + $testRoot + '"'))
    if (!$duplicate.WaitForExit(10000) -or $duplicate.ExitCode -ne 0 -or (Api GET status).instanceId -ne $initial.instanceId) { throw 'Duplicate Worker not rejected' }
    Write-Output 'PASS: private loopback API, authentication, Origin/Host/content-type rejection and single Worker instance'
    $config = $initial.configuration
    $sourceId = [Guid]::NewGuid().ToString()
    $config.sources = @(@{ id = $sourceId; name = 'Process verification'; url = 'https://example.com/feed'; intervalMinutes = 5; automatic = $true; deleted = $false })
    $config.summaryModel = 'verification-model'; $config.aiPaused = $true; $config.allowLoad = $true
    $config = Save-Config $config
    $requestId = [Guid]::NewGuid().ToString()
    $firstRequest = Api POST fetch @{ sourceId = $sourceId; requestId = $requestId }
    $sameRequest = Api POST fetch @{ sourceId = $sourceId; requestId = $requestId }
    if ($firstRequest.runId -ne $sameRequest.runId) { throw 'Repeated request changed run identity' }
    Wait-For { (Api GET status).pendingArticles -eq 10 } 'RSS persists while AI paused'
    if (@(Api GET notifications).Count -ne 0) { throw 'AI pause produced a notification' }
    $before = (Api GET status).sources[0].lastFetchedAt
    Wait-For { (Api GET status).sources[0].lastFetchedAt -ne $before } 'automatic schedule without UI'
    Write-Output 'PASS: durable latest-ten RSS acquisition, paused AI, idempotent manual requests and continued automatic collection'
    $config = (Api GET status).configuration; $config.aiPaused = $false; $config.allowLoad = $false; $config = Save-Config $config
    Wait-For { (Api GET status).aiState -like '*未ロード*' } 'auto-load OFF waits without implicit chat'
    if (@(Api GET notifications).Count -ne 0) { throw 'Auto-load OFF generated a summary' }
    $config = (Api GET status).configuration; $config.allowLoad = $true; $config = Save-Config $config
    Wait-For { (Api GET status).summarizing } 'start AI before forced restart'
    $previousInstance = $endpoint.instanceId
    Stop-Process -Id $workerProcess.Id -Force
    $workerProcess.WaitForExit(10000) | Out-Null
    Start-Worker $previousInstance
    Wait-For { @(Api GET notifications).Count -eq 10 } 'interrupted summaries recover without duplicates'
    $notifications = @(Api GET notifications)
    if ($notifications[0].feedSourceUrl -ne 'https://example.com/feed' -or $notifications[0].sourceUrl -eq $notifications[0].feedSourceUrl) { throw 'Feed source URL and article URL were not separated in the API response' }
    $ids = @($notifications | Select-Object -First 2 | ForEach-Object { $_.notification.id })
    $deleted = Api POST articles/delete @{ ids = $ids }
    if (@($deleted.deletedIds).Count -ne 2) { throw 'Bulk deletion failed' }
    $null = Api POST fetch @{ sourceId = $sourceId; requestId = [Guid]::NewGuid().ToString() }
    Wait-For { @(Api GET notifications).Count -eq 10 } 'deleted feed entries collected as new'
    if (@(Api GET notifications | Where-Object { $ids -contains $_.notification.id }).Count -ne 0) { throw 'Deleted IDs were reused' }
    Write-Output 'PASS: auto-load policy, forced-process recovery, durable deduplication and delete/recollect through authenticated API'
    $logFiles = Get-ChildItem -LiteralPath (Join-Path $testRoot 'l/Ateliers-Dev/Ai-Assistant.Debug/logs') -Filter '*.log'
    foreach ($file in $logFiles) { if ([IO.File]::ReadAllText($file.FullName).Contains($endpoint.token)) { throw 'Secret token leaked into logs' } }
    $null = Api POST shutdown @{}
    if (!$workerProcess.WaitForExit(15000)) { throw 'Graceful Worker shutdown timed out' }
    Write-Output 'PASS: secret-free logs and graceful Worker shutdown'
}
finally {
    if ($null -ne $workerProcess) { $workerProcess.Refresh(); if (!$workerProcess.HasExited) { Stop-Process -Id $workerProcess.Id -Force; $workerProcess.WaitForExit(10000) | Out-Null } }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolved) -notlike 'ateliers-worker-process-*') { throw 'Unexpected test root' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
