param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$deletionRoot = Join-Path $env:TEMP ('ateliers-delete-ui-' + [Guid]::NewGuid().ToString('N'))
$script:appProcess = $null
$script:window = $null
function Find-Id([string]$id) {
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
}
function Wait-For([scriptblock]$check, [string]$label) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do { if (& $check) { return }; Start-Sleep -Milliseconds 100 } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Timed out: ' + $label)
}
function Click([string]$name) {
    Wait-For {
        $items = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name))
        foreach ($item in $items) {
            if ($item.Current.IsEnabled -and $item.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { $script:target = $item; return $true }
        }
        return $false
    } ('button: ' + $name)
    $script:target.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Open-Deletion([int]$count) {
    Click '記事をまとめて削除'
    Wait-For { $null -ne (Find-Id 'ArticleDeletionCount') -and (Find-Id 'ArticleDeletionCount').Current.Name -eq ('選択 0件 / 表示中 ' + $count + '件') } 'deletion dialog and count'
}
function Select-Two {
    $list = Find-Id 'ArticleDeletionList'
    $items = $list.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
    if ($items.Count -lt 2) { throw 'Two visible deletion items required' }
    $items[0].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    $items[1].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    Wait-For { (Find-Id 'ArticleDeletionCount').Current.Name.StartsWith('選択 2件 /') } 'two checked rows'
}
function Start-App {
    $script:appProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru -ArgumentList @('--verify-web-deletion-interactive', ('"' + $deletionRoot + '"'))
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $appProcess.Id)
    Wait-For {
        $script:window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $condition)
        return $null -ne $window -and $null -ne (Find-Id 'DeleteWebArticles') -and (Find-Id 'DeleteWebArticles').Current.IsEnabled
    } 'initial ten articles'
}
function Close-App {
    $appProcess.CloseMainWindow() | Out-Null
    if (!$appProcess.WaitForExit(15000)) { throw 'Native close failed' }
}
try {
    Start-App
    Open-Deletion 10
    Select-Two
    Click 'キャンセル'
    Wait-For { $null -eq (Find-Id 'ArticleDeletionList') -and (Find-Id 'DeleteWebArticles').Current.IsEnabled } 'cancel dialog'
    Open-Deletion 10
    Select-Two
    Click '選択した記事を削除'
    Wait-For { $null -eq (Find-Id 'ArticleDeletionList') -and (Find-Id 'DeleteWebArticles').Current.IsEnabled } 'delete two'
    Open-Deletion 8
    Click 'すべて選択'
    Wait-For { (Find-Id 'ArticleDeletionCount').Current.Name -eq '選択 8件 / 表示中 8件' } 'select all'
    Click '選択解除'
    Wait-For { (Find-Id 'ArticleDeletionCount').Current.Name -eq '選択 0件 / 表示中 8件' } 'clear selection'
    Click 'すべて選択'
    Click '選択した記事を削除'
    Wait-For { $null -eq (Find-Id 'ArticleDeletionList') -and !(Find-Id 'DeleteWebArticles').Current.IsEnabled } 'empty notifications'
    Write-Output 'PASS: native bulk selection, confirmation cancellation, select all/clear, partial and complete deletion'
    Click '取得'
    Wait-For { (Find-Id 'DeleteWebArticles').Current.IsEnabled } 're-fetch after deletion'
    Open-Deletion 10
    Click 'キャンセル'
    Wait-For { $null -eq (Find-Id 'ArticleDeletionList') -and (Find-Id 'DeleteWebArticles').Current.IsEnabled } 'close final dialog'
    Close-App
    Start-App
    Open-Deletion 10
    Click 'キャンセル'
    Wait-For { $null -eq (Find-Id 'ArticleDeletionList') -and (Find-Id 'DeleteWebArticles').Current.IsEnabled } 'close restored dialog'
    Close-App
    Write-Output 'PASS: latest ten reappear as new articles and remain ten after process restart'
}
finally {
    if ($null -ne $appProcess) { $appProcess.Refresh(); if (!$appProcess.HasExited) { Stop-Process -Id $appProcess.Id } }
    $resolved = [IO.Path]::GetFullPath($deletionRoot)
    $tempRoot = [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempRoot -or [IO.Path]::GetFileName($resolved) -notlike 'ateliers-delete-ui-*') { throw 'Unexpected test storage root' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
