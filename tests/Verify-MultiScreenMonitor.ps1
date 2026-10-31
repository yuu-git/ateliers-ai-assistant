param([string]$Model='gemma3:4b',[string]$SecondModel='qwen3.5:4b',[switch]$Single)
$ErrorActionPreference='Stop'
$appPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/Ateliers.Ai.Assistant.Desktop/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Ateliers.Ai.Assistant.Desktop.exe'))
$workerPath=Join-Path (Split-Path $appPath) 'Worker/Ateliers.Ai.Assistant.Worker.exe'
$testRoot=Join-Path $env:TEMP ('ateliers-multi-native-'+[guid]::NewGuid().ToString('N'));$reportPath=$testRoot+'.txt'
$endpointPath=Join-Path $testRoot 'l/Ateliers-Dev/Ai-Assistant.Debug/worker/endpoint.json'
$script:worker=$null;$script:endpoint=$null;$script:picker=$null;$clients=@();$report=[Collections.Generic.List[string]]::new()
function Check([bool]$value,[string]$text){if(!$value){throw $text};$report.Add('PASS: '+$text);Write-Output ('PASS: '+$text)}
function Wait-For([scriptblock]$condition,[string]$text,[int]$seconds=120){$until=[DateTime]::UtcNow.AddSeconds($seconds);do{if(&$condition){return};Start-Sleep -Milliseconds 200}while([DateTime]::UtcNow -lt $until);throw ('Timed out: '+$text)}
function Api([string]$method,[string]$route,$body=$null){$request=@{Uri=$endpoint.address+'api/v1/'+$route;Method=$method;Headers=@{Authorization='Bearer '+$endpoint.token};TimeoutSec=20};if($method -ne 'GET'){$request.ContentType='application/json';$request.Body=if($null -eq $body){'{}'}else{$body|ConvertTo-Json -Depth 20 -Compress}};Invoke-RestMethod @request}
function Status($id){(Api GET monitors).monitors|Where-Object {$_.definition.monitorId -eq $id}|Select-Object -First 1}
function Update($id,[string]$name,[string]$model,[int]$period,[string]$prompt){$m=Status $id;$null=Api PUT ('monitors/'+$id) @{name=$name;settings=@{model=$model;intervalSeconds=$period;prompt=$prompt};revision=$m.definition.revision}}
function Start-Worker{$script:worker=Start-Process $workerPath -ArgumentList @('--screen-monitor-root',('"'+$testRoot+'"')) -WindowStyle Hidden -PassThru;Wait-For {if(!(Test-Path -LiteralPath $endpointPath)){return $false};try{$script:endpoint=Get-Content -LiteralPath $endpointPath -Raw|ConvertFrom-Json;$null=Api GET status;return $true}catch{return $false}} 'Worker startup' 30}
function Show-Client([string]$name){$path=Join-Path $testRoot ($name+'.json');$p=Start-Process $appPath -ArgumentList @('--verify-screen-monitor',('"'+$testRoot+'"'),('"'+$path+'"')) -WindowStyle Hidden -PassThru;$script:clients+=$p;Wait-For {$p.Refresh();$p.HasExited} 'normal MainWindow shutdown' 45;if(!(Test-Path -LiteralPath $path)){throw ('UI report missing, exit '+$p.ExitCode)};$raw=Get-Content -LiteralPath $path -Raw;if($raw -match '^FAIL:'){throw $raw};return ($raw|ConvertFrom-Json)}
Add-Type -AssemblyName UIAutomationClient
Add-Type @'
using System;using System.Runtime.InteropServices;public static class MultiMonitorNative{[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,UIntPtr w,IntPtr l);[DllImport("user32.dll")]public static extern bool ShowWindow(IntPtr h,int c);[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);}
'@
function Control($root,[string]$id){$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Open-Picker($id){
 # この API 単体試験は所有者を直接前面化するため、Desktop → Worker の前面表示許可は検証しない。
 # その回帰確認は --verify-monitor-picker の通常 MainWindow ボタンから行う。
 $pickerOwner=Api GET screen/verification/owner
 [MultiMonitorNative]::ShowWindow([intptr]$pickerOwner.handle,5)|Out-Null
 [MultiMonitorNative]::SetForegroundWindow([intptr]$pickerOwner.handle)|Out-Null
 Start-Sleep -Milliseconds 200
 $null=Api POST ('monitors/'+$id+'/pick')
 $pickerCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,('Ateliers Phase 7 verification '+$worker.Id+' でキャプチャ'))
 Wait-For {
  $state=Status $id
  if(!$state.picking -and $state.error){throw ('Picker failed: '+$state.error)}
  foreach($candidate in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$pickerCondition)){
   try{if(!$candidate.Current.IsOffscreen -and $null -ne (Control $candidate CancelButton)){$script:picker=$candidate;return $true}}catch{}
  };return $false
 } 'Picker controls ready' 30
}
function Select-TestTarget($id){Open-Picker $id;Wait-For {$script:card=$picker.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Ateliers Phase 7 capture verification'));$null -ne $script:card} 'synthetic target listed' 10;([System.Windows.Automation.SelectionItemPattern]$card.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select();$ok=Control $picker AcceptButton;([System.Windows.Automation.InvokePattern]$ok.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();Wait-For {!(Status $id).picking} 'Picker completed'}
try{
 New-Item -ItemType Directory -Path $testRoot|Out-Null
 $settingsDir=Join-Path $testRoot 'r/Ateliers-Dev/Ai-Assistant.Debug';New-Item -ItemType Directory -Path $settingsDir -Force|Out-Null
 @{version=1;summaryModel=$null;allowLoad=$true;allowUnload=$true;aiPaused=$false;sources=@();screenMonitor=@{model=$Model;intervalSeconds=30;prompt='検証用ウィンドウの内容を日本語で一文で説明してください。'}}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $settingsDir 'worker-settings.json')
 Start-Worker;$initial=Api GET monitors;$a=$initial.monitors[0].definition.monitorId
 Check ($initial.monitors.Count -eq 1 -and !(Status $a).active) 'legacy single definition migrates to one stopped stable ID'
 $target=Api POST screen/verification/target;$owner=Api GET screen/verification/owner
 Select-TestTarget $a;Update $a '監視A' $Model 30 '検証画面のタイトルを日本語で一文で説明してください。';$null=Api POST ('monitors/'+$a+'/start')
 $b=$null
 if(!$Single){$created=Api POST monitors @{name='監視B'};$b=$created.definition.monitorId;Select-TestTarget $b;Update $b '監視B' $Model 60 '検証画面に見えるものを短い日本語で説明してください。';$null=Api POST ('monitors/'+$b+'/start')}
 Wait-For {@((Api GET ('monitors/'+$a)).comments|Where-Object {$_.status -eq '完了'}).Count -ge 1} 'A Vision result' 180
 if($b){Wait-For {@((Api GET ('monitors/'+$b)).comments|Where-Object {$_.status -eq '完了'}).Count -ge 1} 'B Vision result' 180;Check ((Status $a).targetName -eq (Status $b).targetName) 'same target selected twice through real standard Picker with independent definitions'}
 $first=Show-Client client1;Check ($first.monitors.Count -eq $(if($Single){1}else{2})) 'real MainWindow lists sessions, switches comments and renders timestamped Markdown'
 Check (@((Invoke-RestMethod 'http://localhost:11434/api/ps').models|Where-Object {$_.name -eq $Model}).Count -eq 1) 'normal Desktop shutdown protects monitored model'
 $beforeA=(Status $a).captures;$beforeB=if($b){(Status $b).captures}else{0}
 Wait-For {(Status $a).captures -gt $beforeA} 'A continues after Desktop exit' 100
 if($b){Wait-For {(Status $b).captures -gt $beforeB} 'B continues after Desktop exit' 100}
 $second=Show-Client client2;Check ($second.instanceId -eq $first.instanceId) 'Desktop restart restores same Worker and separate comment histories'
 if($b){
 $null=Api POST ('monitors/'+$a+'/stop');Wait-For {!(Status $a).processing} 'A actual cancellation';Check ((Status $b).active -and @((Invoke-RestMethod 'http://localhost:11434/api/ps').models|Where-Object {$_.name -eq $Model}).Count -eq 1) 'stopping A retains B and shared model'
 Open-Picker $a;$blocked=$false;try{$null=Api POST ('monitors/'+$b+'/pick')}catch{$blocked=$true};$oldTarget=(Status $a).targetName;$null=Api POST ('monitors/'+$a+'/stop');Check ((Status $a).picking -and $blocked) 'cancel token retains Picker ownership until OS selection actually completes'
 $cancel=Control $picker CancelButton;([System.Windows.Automation.InvokePattern]$cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();Wait-For {!(Status $a).picking} 'Picker cancel';Check ((Status $a).targetName -eq $oldTarget -and (Status $b).active) 'Picker cancellation preserves A target and B execution'
 $other=Api POST screen/verification/target;$null=Api POST ('monitors/'+$a+'/verification/select') @{handle=$other.handle;monitor=$false}
 Update $a '監視A' $SecondModel 10 'この検証用画面について日本語で短くコメントしてください。';$null=Api POST ('monitors/'+$a+'/start')
 Wait-For {@((Api GET ('monitors/'+$a)).comments|Where-Object {$_.model -eq $SecondModel -and $_.status -eq '完了'}).Count -gt 0} 'second installed Vision model on a separate target' 240
 Check ((Status $a).definition.settings.model -ne (Status $b).definition.settings.model -and (Status $a).definition.settings.intervalSeconds -ne (Status $b).definition.settings.intervalSeconds) 'separate targets, models, periods and prompts produce isolated comments'
 $before=(Status $b).captures;$null=Api POST ('monitors/'+$a+'/delete');Wait-For {$null -eq (Status $a)} 'A delete completion';Wait-For {(Status $b).captures -gt $before} 'B progresses after deleting A' 100;Check $true 'deleting running A releases only its resources and B keeps progressing'
 $a=$b
 }
 $null=Api POST ai/pause @{paused=$true};Wait-For {!(Status $a).processing} 'pause completes';$count=(Status $a).captures;Start-Sleep -Seconds 3;Check ((Status $a).captures -eq $count) 'global pause cancels capture and inference without starting overlapping work';$null=Api POST ai/pause @{paused=$false};Wait-For {(Status $a).captures -gt $count} 'fresh frame after resume' 100
 [MultiMonitorNative]::PostMessage([intptr]$owner.handle,0x8002,[uintptr]1,[intptr]0x205)|Out-Null
 Wait-For {$script:menu=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$worker.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32768')));$null -ne $script:menu} 'tray menu'
 $stop=$menu.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'全監視を停止'));([System.Windows.Automation.InvokePattern]$stop.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke();Wait-For {@((Api GET monitors).monitors|Where-Object {$_.active -or $_.processing}).Count -eq 0} 'tray stops actual work';Check $true 'tray all-stop works while Desktop is closed'
 Wait-For {@((Invoke-RestMethod 'http://localhost:11434/api/ps').models|Where-Object {$_.name -in @($Model,$SecondModel)}).Count -eq 0} 'last shared model auto unload' 30
 $saved=@((Api GET monitors).monitors|ForEach-Object {$_.definition.monitorId});$null=Api POST shutdown;Wait-For {$worker.Refresh();$worker.HasExited} 'graceful Worker shutdown';Remove-Item -LiteralPath $endpointPath -ErrorAction SilentlyContinue;Start-Worker;$restored=Api GET monitors
 Check (@($restored.monitors|Where-Object {$_.active -or $_.targetName}).Count -eq 0 -and @($restored.monitors|ForEach-Object {$_.definition.monitorId})[0] -eq $saved[0]) 'Worker restart retains definitions and IDs, never restarts capture'
 foreach($item in $restored.monitors){Check ((Api GET ('monitors/'+$item.definition.monitorId)).comments.Count -eq 0) 'comments are memory only after Worker restart'}
 $report.Add('PASS: ALL MULTI SCREEN MONITOR CHECKS');Write-Output 'PASS: ALL MULTI SCREEN MONITOR CHECKS'
}catch{$report.Add('FAIL: '+$_.Exception.Message);throw}
finally{
 if($null -ne $picker){try{$c=Control $picker CancelButton;if($null -ne $c){([System.Windows.Automation.InvokePattern]$c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()}}catch{}}
 if($null -ne $worker){try{$null=Api POST shutdown}catch{};$worker.WaitForExit(15000)|Out-Null}
 foreach($p in @($worker)+$clients){if($null -ne $p){$p.Refresh();if(!$p.HasExited){Stop-Process -Id $p.Id -Force}}}
 $report|Set-Content -LiteralPath $reportPath;Write-Output ('Report: '+$reportPath)
 $full=[IO.Path]::GetFullPath($testRoot);if([IO.Path]::GetDirectoryName($full) -ne [IO.Path]::GetFullPath($env:TEMP).TrimEnd([IO.Path]::DirectorySeparatorChar) -or [IO.Path]::GetFileName($full) -notlike 'ateliers-multi-native-*'){throw 'Unsafe root'};if(Test-Path -LiteralPath $full){Remove-Item -LiteralPath $full -Recurse -Force}
}
