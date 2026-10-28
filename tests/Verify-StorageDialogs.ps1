param([string]$ExecutablePath = '', [ValidateSet('Debug', 'Release')][string]$ExpectedEnvironment = 'Release')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
if ($ExecutablePath) { $appPath = (Resolve-Path -LiteralPath $ExecutablePath).Path }
$storageRoot = Join-Path $env:TEMP ('ateliers-storage-dialogs-' + [Guid]::NewGuid().ToString('N'))
$titleSuffix = [Guid]::NewGuid().ToString('N')
$firstTitle = 'UI 保存検証 ' + $titleSuffix
$exitTitle = 'UI 終了検証 ' + $titleSuffix
$script:appProcess = $null
$script:window = $null
function Find-Name([string]$name) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Id([string]$id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-For([scriptblock]$check, [string]$label) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { if (& $check) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Timed out: ' + $label)
}
function Click([string]$name) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
    $button = $null
    Wait-For {
        foreach ($item in $window.FindAll([Windows.Automation.TreeScope]::Descendants, $condition)) {
            if ($item.Current.IsEnabled -and $item.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $script:targetButton = $item; return $true }
        }
        return $false
    } ('button ' + $name)
    $script:targetButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-Input([string]$value) { (Find-Id 'ChatInput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($value) }
function Input-Text { return (Find-Id 'ChatInput').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
function Select-Item($item) {
    while ($null -ne $item) {
        $pattern = $null
        if ($item.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); return }
        $item = [Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($item)
    }
    throw 'Selection pattern not found'
}
function Start-TestApp {
    $startArguments = @{ FilePath = $appPath; WorkingDirectory = (Split-Path $appPath); WindowStyle = 'Hidden'; PassThru = $true }
    if (!$ExecutablePath) { $startArguments.ArgumentList = @('--verify-storage-interactive', ('"' + $storageRoot + '"')) }
    $script:appProcess = Start-Process @startArguments
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    Wait-For {
        $script:window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
        return $null -ne $window -and $null -ne (Find-Id 'ChatInput') -and (Find-Id 'ChatInput').Current.IsEnabled
    } 'window initialized'
}
try {
    Start-TestApp
    Set-Input '未送信のまま保存するテキスト'
    Click '会話を保存'
    Wait-For { $null -ne (Find-Id 'ConversationTitle') } 'title dialog'
    (Find-Id 'ConversationTitle').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($firstTitle)
    Click '保存'
    Wait-For { $null -eq (Find-Id 'ConversationTitle') -and (Find-Name '新しい会話').Current.IsEnabled } 'save completed'
    Click '新しい会話'
    Wait-For { (Input-Text) -eq '' } 'new empty conversation'
    Set-Input '離脱を確認する入力'
    Select-Item (Find-Name '設定')
    Click 'キャンセル'
    Wait-For { (Input-Text) -eq '離脱を確認する入力' -and (Find-Id 'ChatInput').Current.IsEnabled } 'navigation canceled'
    Select-Item (Find-Name '設定')
    Click '保存せず破棄'
    Wait-For { $null -ne (Find-Name '設定フォルダーを開く') -and (Find-Name '設定フォルダーを開く').Current.IsEnabled } 'settings page'
    $pathValues = @($window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
        $pattern = $null
        if ($_.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) { $pattern.Current.Value }
    })
    if (!$ExecutablePath) {
        if (@($pathValues | Where-Object { $_ -like ($storageRoot + '*') }).Count -ne 4) { throw 'Displayed storage paths differ from isolated fixed paths' }
        if ($null -eq (Find-Name 'Debug（開発用データ）')) { throw 'Debug storage environment label missing' }
    }
    else {
        $applicationDirectory = if ($ExpectedEnvironment -eq 'Debug') { 'Ai-Assistant.Debug' } else { 'Ai-Assistant' }
        $expectedLabel = if ($ExpectedEnvironment -eq 'Debug') { 'Debug（開発用データ）' } else { 'Release（通常利用データ）' }
        if ($null -eq (Find-Name $expectedLabel)) { throw 'Storage environment label missing' }
        $roaming = Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('Ateliers-Dev/' + $applicationDirectory)
        $local = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Ateliers-Dev/' + $applicationDirectory)
        foreach ($expected in @($roaming, $local, (Join-Path $local 'data'), (Join-Path $local 'logs'))) {
            if ($expected -notin $pathValues) { throw 'Published fixed path differs from displayed path' }
        }
    }
    Write-Output 'PASS: native save/title dialog, navigation cancel/discard, and all four read-only storage paths'
    Select-Item (Find-Name 'AI チャット')
    Wait-For { (Find-Id 'ChatInput').Current.IsEnabled } 'chat returned'
    Set-Input '終了前に保存する入力'
    $appProcess.CloseMainWindow() | Out-Null
    Click '保存'
    Wait-For { $null -ne (Find-Id 'ConversationTitle') } 'close title dialog'
    (Find-Id 'ConversationTitle').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($exitTitle)
    Click '保存'
    if (!$appProcess.WaitForExit(20000)) { throw 'Window did not close after native save dialog' }
    Start-TestApp
    Wait-For { $null -ne (Find-Name $exitTitle) } 'persisted title list'
    Select-Item (Find-Name $exitTitle)
    Click '会話を開く'
    Wait-For { (Input-Text) -eq '終了前に保存する入力' } 'draft restored after restart'
    Click '削除'; Click 'キャンセル'
    Wait-For { (Find-Name '削除').Current.IsEnabled } 'delete canceled'
    Click '削除'; Click '削除'
    Wait-For { (Input-Text) -eq '' -and $null -eq (Find-Name $exitTitle) } 'confirmed delete clears draft'
    Select-Item (Find-Name $firstTitle)
    Click '削除'; Click '削除'
    Wait-For { $null -eq (Find-Name $firstTitle) } 'only the other verification thread removed'
    $appProcess.CloseMainWindow() | Out-Null
    if (!$appProcess.WaitForExit(20000)) { throw 'Final close failed' }
    Write-Output 'PASS: real exit save dialog, process restart, draft restore, and deletion cancel/confirm'
}
finally {
    if ($null -ne $appProcess) {
        $appProcess.Refresh()
        if (!$appProcess.HasExited) { $appProcess.CloseMainWindow() | Out-Null; if (!$appProcess.WaitForExit(3000)) { Stop-Process -Id $appProcess.Id } }
    }
}
$resolvedRoot = [IO.Path]::GetFullPath($storageRoot)
if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolvedRoot) -notlike 'ateliers-storage-dialogs-*') { throw 'Unexpected cleanup root' }
if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
