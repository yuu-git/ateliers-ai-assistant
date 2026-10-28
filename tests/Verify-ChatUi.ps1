param([string]$Model = 'qwen2.5-coder:3b', [string]$ExecutablePath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$initialRunning = @((Invoke-RestMethod -Uri 'http://localhost:11434/api/ps' -TimeoutSec 5).models | ForEach-Object { $_.name })
if ($initialRunning.Count -gt 0) { throw 'Run this lifecycle UI test when Ollama has no loaded models.' }
$alternative = @((Invoke-RestMethod -Uri 'http://localhost:11434/api/tags' -TimeoutSec 5).models |
    Where-Object { $_.name -ne $Model -and $_.name -notin $initialRunning } | ForEach-Object { $_.name }) | Select-Object -First 1

# 先に README の Unpackaged ビルドを実行する。指定モデルを導入済みの Ollama が必要。
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
if ($ExecutablePath) { $appPath = (Resolve-Path -LiteralPath $ExecutablePath).Path }
$appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru
function Find-Named($root, [string]$name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Until([scriptblock]$check, [string]$failure, [int]$seconds = 45) {
    $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        if (& $check) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $failure
}
function Invoke-Button($button) {
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}
function Has-Text([string]$text) {
    $elements = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        if ($element.Current.Name -like "*$text*") { return $true }
    }
    return $false
}
function Select-ChatModel([string]$name) {
    $expand = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 200
    $item = Find-Named $combo $name
    if ($null -eq $item) { $item = Find-Named $window $name }
    if ($null -eq $item) { throw "Model not found in ComboBox: $name" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    # 選択後は ComboBox 自身が閉じる。切替開始で無効化されたコントロールに追加操作しない。
}
try {
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    $script:window = $null
    Wait-Until {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw "Application exited: $($appProcess.ExitCode)" }
        $script:window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $processCondition)
        return $null -ne $window -and $window.Current.Name -eq 'Ateliers AI Assistant'
    } 'Chat window did not open. Check Windows App Runtime / build settings.' 10
    $combo = Find-Named $window 'モデル'
    $sendButton = Find-Named $window '送信'
    $cancelButton = Find-Named $window '中止'
    $newButton = Find-Named $window '新しい会話'
    $loadButton = Find-Named $window '起動'
    $stopButton = Find-Named $window '停止'
    if ($null -eq $combo -or $null -eq $sendButton -or $null -eq $cancelButton) { throw 'Chat controls not found' }
    Wait-Until { $combo.Current.IsEnabled } 'Model list did not finish loading' 15
    if ($sendButton.Current.IsEnabled) {
        foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            Write-Output "$($element.Current.ControlType.ProgrammaticName): $($element.Current.Name) enabled=$($element.Current.IsEnabled)"
        }
        throw 'Send should be disabled before model selection'
    }
    Select-ChatModel $Model
    Wait-Until { $loadButton.Current.IsEnabled } 'Load did not enable' 10
    Invoke-Button $loadButton
    Wait-Until { $stopButton.Current.IsEnabled } 'Load did not finish'
    $running = (Invoke-RestMethod -Uri 'http://localhost:11434/api/ps' -TimeoutSec 5).models
    if ($Model -notin @($running | ForEach-Object { $_.name })) { throw 'Loaded model not found in /api/ps' }
    Write-Output 'PASS: explicit model load and actual /api/ps state'
    $script:editor = $null
    try { Wait-Until { $script:editor = Find-Named $window 'メッセージ'; $null -ne $editor } 'Chat input not found' 5 }
    catch {
        Write-Output "Window bounds: $($window.Current.BoundingRectangle)"
        foreach ($element in $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            Write-Output "$($element.Current.ControlType.ProgrammaticName): $($element.Current.Name) [$($element.Current.AutomationId)]"
        }
        throw
    }
    $value = $editor.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue('合言葉は青空です。了解、とだけ日本語で答えてください。')
    Wait-Until { $sendButton.Current.IsEnabled } 'Input binding did not enable Send' 5
    Invoke-Button $sendButton
    Wait-Until { $value.Current.Value -eq '' } 'First Send did not start' 5
    Wait-Until { $newButton.Current.IsEnabled } 'First reply did not finish'
    # ブラウザー DOM は Verify-MarkdownDisplay.ps1 で検証し、ここではネイティブの状態を確認する。
    if (!(Has-Text '送信できます。')) { throw 'First reply failed' }
    Start-Sleep -Milliseconds 300
    if ($null -ne (Find-Named $window 'チャット原文')) { throw 'Markdown display fell back to raw text' }
    if ($value.Current.Value -ne '') { throw 'Input was not cleared' }
    Write-Output 'PASS: model selection, input binding, and first Ollama response'

    $value.SetValue('さっきの合言葉だけを答えてください。')
    Wait-Until { $sendButton.Current.IsEnabled } 'Second Send did not enable' 5
    Invoke-Button $sendButton
    Wait-Until { $value.Current.Value -eq '' } 'Second Send did not start' 5
    Wait-Until { $newButton.Current.IsEnabled } 'Second reply did not finish'
    if (!(Has-Text '送信できます。')) { throw 'Second reply failed' }
    Write-Output 'PASS: continued conversation'

    $value.SetValue('1から1000までの整数を、一行ずつ説明付きで順番に列挙してください。')
    Wait-Until { $sendButton.Current.IsEnabled } 'Third Send did not enable' 5
    Invoke-Button $sendButton
    Wait-Until { $cancelButton.Current.IsEnabled } 'Cancel did not enable' 5
    if ($stopButton.Current.IsEnabled) { throw 'Model stop must be disabled during chat' }
    Invoke-Button $cancelButton
    Wait-Until { $newButton.Current.IsEnabled } 'Cancellation did not restore controls' 10
    if (!(Has-Text '応答を中止しました。')) { throw 'Cancellation status was not displayed' }
    Write-Output 'PASS: cancellation and UI recovery'
    Invoke-Button $newButton
    Wait-Until { Has-Text '新しい会話を開始しました。' } 'New conversation did not execute' 5
    Write-Output 'PASS: new conversation'

    $navigation = Find-Named $window 'モデル管理'
    $navigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $script:runningList = $null
    Wait-Until { $script:runningList = Find-Named $window 'ロード中モデル'; $null -ne $runningList -and $runningList.Current.IsEnabled } 'Model management did not load' 15
    $script:row = $null
    Wait-Until { $script:row = Find-Named $runningList $Model; $null -ne $row } 'Model management row not found' 10
    while ($row.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) {
        $row = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($row)
        if ($null -eq $row) { throw 'List item container not found' }
    }
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $manualStop = Find-Named $window '選択モデルを停止'
    Wait-Until { $manualStop.Current.IsEnabled } 'Manual unload did not enable' 5
    Invoke-Button $manualStop
    Wait-Until { Has-Text 'ロード中のモデルはありません。' } 'Empty loaded state did not appear' 15
    Write-Output 'PASS: model management list, explicit unload, and empty state'

    $chatNavigation = Find-Named $window 'AI チャット'
    $chatNavigation.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Until { $loadButton.Current.IsEnabled } 'Chat state did not reflect manual unload' 5
    Invoke-Button $loadButton
    Wait-Until { $stopButton.Current.IsEnabled } 'Reload did not finish'
    if ($null -ne $alternative) {
        Select-ChatModel $alternative
        Wait-Until { $loadButton.Current.IsEnabled } 'Model change did not finish' 15
        $running = (Invoke-RestMethod -Uri 'http://localhost:11434/api/ps' -TimeoutSec 5).models
        if ($Model -in @($running | ForEach-Object { $_.name }) -or $alternative -in @($running | ForEach-Object { $_.name })) {
            throw 'Model change should unload old model and not load the new model'
        }
        Write-Output 'PASS: model change unloads managed old model without loading new model'
        Select-ChatModel $Model
        Wait-Until { $loadButton.Current.IsEnabled } 'Original model selection did not finish' 10
    }
    if ($loadButton.Current.IsEnabled) { Invoke-Button $loadButton }
    Wait-Until { $stopButton.Current.IsEnabled } 'Final load did not finish'
    $appProcess.CloseMainWindow() | Out-Null
    if (!$appProcess.WaitForExit(8000)) { throw 'Graceful shutdown exceeded its deadline' }
    $running = (Invoke-RestMethod -Uri 'http://localhost:11434/api/ps' -TimeoutSec 5).models
    if ($Model -in @($running | ForEach-Object { $_.name })) { throw 'Managed model remained loaded after exit' }
    Write-Output 'PASS: graceful window close unloads the managed model'
}
finally {
    $appProcess.Refresh()
    if (!$appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        if (!$appProcess.WaitForExit(8000)) { Stop-Process -Id $appProcess.Id }
    }
}
