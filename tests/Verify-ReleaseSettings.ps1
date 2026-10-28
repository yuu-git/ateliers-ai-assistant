param([string]$ExecutablePath = '', [switch]$OpenExplorer, [ValidateSet('Debug', 'Release')][string]$ExpectedEnvironment = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (!$ExecutablePath) { $ExecutablePath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/Ateliers.Ai.Assistant.Desktop.exe' }
$appProcess = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath) -WindowStyle Hidden -PassThru
$script:window = $null
$openedExplorer = $null
function Find-ReleaseName([string]$name) { return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)) }
function Wait-Release([scriptblock]$check, [string]$label) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw ('Published app exited: ' + $appProcess.ExitCode) }
        if (& $check) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Timed out: ' + $label)
}
try {
    $processCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    Wait-Release {
        $script:window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $processCondition)
        return $null -ne $window -and $null -ne (Find-ReleaseName '設定') -and (Find-ReleaseName '設定').Current.IsEnabled
    } 'published window'
    (Find-ReleaseName '設定').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Release { $null -ne (Find-ReleaseName 'DB フォルダーを開く') -and (Find-ReleaseName 'DB フォルダーを開く').Current.IsEnabled } 'published settings'
    $displayed = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
        $pattern = $null
        if ($_.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.Current.Value }
    })
    $applicationDirectory = if ($ExpectedEnvironment -eq 'Debug') { 'Ai-Assistant.Debug' } else { 'Ai-Assistant' }
    $expectedLabel = if ($ExpectedEnvironment -eq 'Debug') { 'Debug（開発用データ）' } else { 'Release（通常利用データ）' }
    if ($null -eq (Find-ReleaseName $expectedLabel)) { throw 'Storage environment label differs from build configuration' }
    $roaming = Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('Ateliers-Dev/' + $applicationDirectory)
    $local = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Ateliers-Dev/' + $applicationDirectory)
    foreach ($expected in @($roaming, $local, (Join-Path $local 'data'), (Join-Path $local 'logs'))) { if ($expected -notin $displayed) { throw 'Published path binding mismatch' } }
    Write-Output ('PASS: ' + $ExpectedEnvironment + ' window, visible environment, settings commands, and all fixed path bindings')
    if ($OpenExplorer) {
        $shell = New-Object -ComObject Shell.Application
        $initialHandles = @($shell.Windows() | ForEach-Object { $_.HWND })
        (Find-ReleaseName 'DB フォルダーを開く').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $target = Join-Path $local 'data'
        Wait-Release {
            foreach ($explorer in $shell.Windows()) {
                try { if ($explorer.Document.Folder.Self.Path -eq $target) { if ($explorer.HWND -notin $initialHandles) { $script:openedExplorer = $explorer }; return $true } } catch {}
            }
            return $false
        } 'real Explorer fixed folder launch'
        Write-Output 'PASS: real Explorer opens the resolved DB folder'
    }
    $appProcess.CloseMainWindow() | Out-Null
    if (!$appProcess.WaitForExit(15000)) { throw 'Published Release close failed' }
    Write-Output ('PASS: ' + $ExpectedEnvironment + ' normal shutdown')
}
finally {
    if ($null -ne $openedExplorer) { $openedExplorer.Quit() }
    $appProcess.Refresh()
    if (!$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; if (!$appProcess.WaitForExit(3000)) { Stop-Process -Id $appProcess.Id } }
}
