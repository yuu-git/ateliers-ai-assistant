param()
$ErrorActionPreference='Stop'
$root=Join-Path $env:TEMP ('ateliers-app-targets-'+[guid]::NewGuid().ToString('N'))
$worker=$null;$browser=$null;$endpoint=$null;$task=$null;$taskOwned=$false;$minimized=$false
$workerExe=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Worker/Ateliers.Ai.Assistant.Worker.exe'))
function Wait-For([scriptblock]$predicate,[string]$description){$until=[DateTime]::UtcNow.AddSeconds(40);do{if(&$predicate){return};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $until);throw ('Timed out: '+$description)}
function Api([string]$method,[string]$route,$body=$null){$request=@{Uri=$endpoint.address+'api/v1/'+$route;Method=$method;Headers=@{Authorization='Bearer '+$endpoint.token};TimeoutSec=15};if($method -ne 'GET'){$request.ContentType='application/json';$request.Body=if($body){$body|ConvertTo-Json -Depth 10 -Compress}else{'{}'}};Invoke-RestMethod @request}
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;using System.Runtime.InteropServices;public static class ApplicationTargetNative{[DllImport("user32.dll")]public static extern bool IsIconic(IntPtr h);[DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);}
'@
try{
 New-Item -ItemType Directory -Path $root|Out-Null
 $html=Join-Path $root sample.html;'<html><head><title>Ateliers isolated browser capture test</title></head><body><h1>Independent monitor A</h1><p>Local synthetic content only.</p></body></html>'|Set-Content -LiteralPath $html
 $browser=Start-Process 'C:/Program Files/Google/Chrome/Application/chrome.exe' -ArgumentList @('--user-data-dir="'+(Join-Path $root profile)+'"','--app="'+([uri]$html).AbsoluteUri+'"','--no-first-run','--no-default-browser-check','--disable-background-networking') -WindowStyle Hidden -PassThru
 Wait-For {$script:browserWindow=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Ateliers isolated browser capture test'));$null -ne $script:browserWindow} 'isolated Chrome app window'
 $chromeHandle=$browserWindow.Current.NativeWindowHandle
 $task=Get-Process Taskmgr -ErrorAction SilentlyContinue|Where-Object {$_.MainWindowHandle -ne 0}|Select-Object -First 1
 if($null -eq $task){$task=Start-Process "$env:WINDIR/System32/Taskmgr.exe" -WindowStyle Hidden -PassThru;$taskOwned=$true;Wait-For {$task.Refresh();$task.MainWindowHandle -ne 0} 'Task Manager window'}
 $taskHandle=$task.MainWindowHandle;$minimized=[ApplicationTargetNative]::IsIconic($taskHandle);if($minimized){[ApplicationTargetNative]::ShowWindow($taskHandle,9)|Out-Null}
 $worker=Start-Process $workerExe -ArgumentList @('--screen-monitor-fake-root',('"'+$root+'"')) -WindowStyle Hidden -PassThru
 $file=Join-Path $root 'l/Ateliers-Dev/Ai-Assistant.Debug/worker/endpoint.json';Wait-For {Test-Path -LiteralPath $file} 'isolated Worker';$endpoint=Get-Content -LiteralPath $file|ConvertFrom-Json
 $config=(Api GET status).configuration;$config.allowLoad=$true;$config.allowUnload=$true;$null=Api PUT configuration $config
 $ids=@();foreach($entry in @(@{Name='Chrome';Handle=$chromeHandle},@{Name='Task Manager';Handle=$taskHandle})){
  $created=Api POST monitors @{name=$entry.Name};$id=$created.definition.monitorId;$ids+=$id
  $null=Api PUT ('monitors/'+$id) @{name=$entry.Name;revision=$created.definition.revision;settings=@{model='verification-model';intervalSeconds=10;prompt='Capture test'}}
  $null=Api POST ('monitors/'+$id+'/verification/select') @{handle=[long]$entry.Handle;monitor=$false}
  $null=Api POST ('monitors/'+$id+'/start')
 }
 Wait-For {$list=Api GET monitors;@($list.monitors|Where-Object {$_.captures -ge 2 -and $_.active}).Count -eq 2} 'both real application targets repeatedly captured'
 $null=Api POST ('monitors/'+$ids[0]+'/delete');$before=(Api GET ('monitors/'+$ids[1])).comments.Count
 Wait-For {(Api GET ('monitors/'+$ids[1])).comments.Count -gt $before} 'Task Manager keeps capturing after Chrome monitor deletion'
 Write-Output 'PASS: actual isolated Chrome and Task Manager targets repeatedly captured in independent sessions'
 Write-Output 'PASS: deleting Chrome monitor leaves Task Manager monitor running'
 Write-Output 'INFO: real WGC with in-process fake inference; application screenshots never sent to Ollama or saved'
}finally{
 if($null -ne $endpoint){try{$null=Api POST shutdown}catch{}}
 if($null -ne $worker){$worker.WaitForExit(12000)|Out-Null;if(!$worker.HasExited){$worker.Kill($true)}}
 if($minimized -and $null -ne $task){[ApplicationTargetNative]::ShowWindow($task.MainWindowHandle,6)|Out-Null}
 if($taskOwned -and $null -ne $task){$task.Refresh();if(!$task.HasExited){$task.CloseMainWindow()|Out-Null}}
 if($null -ne $browser){$browser.Refresh();if(!$browser.HasExited){$browser.Kill($true);$browser.WaitForExit(10000)|Out-Null}}
 $full=[IO.Path]::GetFullPath($root);if([IO.Path]::GetDirectoryName($full) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($full) -notlike 'ateliers-app-targets-*'){throw 'Unsafe root'}
 if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}
}
