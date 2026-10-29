param()
$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'
$testRoot = Join-Path $env:TEMP ('ateliers-ui-layout-' + [Guid]::NewGuid().ToString('N'))
$outputPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/ui-refactoring'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$reportPath = Join-Path $testRoot 'report.txt'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
[UiCapture]::SetProcessDPIAware() | Out-Null
$uiProcess = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -WindowStyle Hidden -PassThru -ArgumentList @('--verify-ui-layout', ('"' + $reportPath + '"'), ('"' + $testRoot + '"'))
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $handled = ''
    while ([DateTime]::UtcNow -lt $deadline) {
        $uiProcess.Refresh()
        if ($uiProcess.HasExited) { break }
        $request = Join-Path $testRoot 'capture-request.txt'
        if (Test-Path -LiteralPath $request) {
            $stage = [IO.File]::ReadAllText($request).Trim()
            if ($stage -and $stage -ne $handled) {
                if ($stage -notmatch '^[a-z-]+$') { throw 'Invalid capture stage' }
                $windowHandle = $uiProcess.MainWindowHandle
                if ($windowHandle -eq [IntPtr]::Zero) { throw 'Window handle unavailable' }
                [UiCapture]::ShowWindow($windowHandle, 9) | Out-Null
                [UiCapture]::SetWindowPos($windowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x43) | Out-Null
                [UiCapture]::SetForegroundWindow($windowHandle) | Out-Null
                Start-Sleep -Milliseconds 500
                [UiCapture]::DwmFlush() | Out-Null
                $bounds = New-Object UiCapture+Rect
                if (![UiCapture]::GetWindowRect($windowHandle, [ref]$bounds)) { throw 'Window bounds unavailable' }
                $bitmap = New-Object Drawing.Bitmap ($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top)
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
                    $visiblePixels = 0; $sampledPixels = 0
                    for ($sampleX = 24; $sampleX -lt $bitmap.Width - 24; $sampleX += 32) {
                        for ($sampleY = 64; $sampleY -lt $bitmap.Height - 24; $sampleY += 32) {
                            $pixel = $bitmap.GetPixel($sampleX, $sampleY); $sampledPixels++
                            if ($pixel.R + $pixel.G + $pixel.B -gt 24) { $visiblePixels++ }
                        }
                    }
                    if ($visiblePixels -lt $sampledPixels / 10) { throw ('Captured XAML content is blank: ' + $stage) }
                    $bitmap.Save((Join-Path $outputPath ($stage + '.png')), [Drawing.Imaging.ImageFormat]::Png)
                } finally {
                    $graphics.Dispose(); $bitmap.Dispose()
                    [UiCapture]::SetWindowPos($windowHandle, [IntPtr](-2), 0, 0, 0, 0, 0x03) | Out-Null
                }
                if ($stage -eq 'bulk-delete') {
                    $window = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
                    $list = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ArticleDeletionList'))
                    $items = $list.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))
                    if ($items.Count -ne 4) { throw ('Deletion scope differs from source-filtered list: ' + $items.Count) }
                    $items[1].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
                    Start-Sleep -Milliseconds 150
                    $warning = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ArticleDeletionWarning'))
                    if ($warning.Current.Name -notmatch 'ブックマーク付き 1件・タグ付き 1件') { throw ('Deletion metadata warning differs: ' + $warning.Current.Name) }
                    $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '選択した記事を削除'))
                    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                }
                if ($stage -like 'single-delete-*') {
                    $window = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
                    if ($stage -eq 'single-delete-settings') {
                        $preference = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'ConfirmSingleArticleDeletion'))
                        if ($preference.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) { throw 'Suppression setting was not reflected in native checkbox' }
                        $preference.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
                    } else {
                        $skip = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, 'SkipSingleArticleDeletionConfirmation'))
                        $skip.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
                        $action = if ($stage -eq 'single-delete-cancel') { 'キャンセル' } else { 'この記事を削除' }
                        $button = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $action))
                        $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                    }
                }
                [IO.File]::WriteAllText((Join-Path $testRoot ($stage + '.captured')), 'ok')
                $handled = $stage
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if (!$uiProcess.HasExited) { throw 'UI refactoring verification timed out' }
    if (!(Test-Path -LiteralPath $reportPath)) { throw ('Verification report missing: ' + $uiProcess.ExitCode) }
    $report = [IO.File]::ReadAllText($reportPath)
    [IO.File]::WriteAllText((Join-Path $outputPath 'verification.txt'), $report)
    Write-Output $report
    if ($report -notmatch 'PASS: ALL UI REFACTORING CHECKS') { throw 'UI refactoring verification failed' }
    Write-Output ('Screenshots: ' + $outputPath)
}
finally {
    $uiProcess.Refresh()
    if (!$uiProcess.HasExited) { Stop-Process -Id $uiProcess.Id -Force; $uiProcess.WaitForExit(5000) | Out-Null }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($resolved) -notlike 'ateliers-ui-layout-*') { throw 'Unexpected test directory' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
