$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\StartupManager.Common.ps1')
. (Join-Path $root 'scripts\StartupEngine.ps1')
$script:events=New-Object System.Collections.Generic.List[string]
$script:clock=[datetime]'2020-01-01'
function Get-Date {$script:clock}
function Start-Sleep {param($Milliseconds,$Seconds);$script:clock=$script:clock.AddMilliseconds([double]$Milliseconds+[double]$Seconds*1000)}
function Write-StartupLog($Message){$script:events.Add('log:'+$Message)}
function Start-ManagedApp($App){$script:events.Add('start:'+$App.Name);if($App.Name -eq 'Broken'){throw 'Launch failed'};[pscustomobject]@{Process=$null}}
function Test-ManagedAppReady($App,$Launch){$script:events.Add('check:'+$App.Name);if($App.Name -eq 'Never'){return $false};if($App.Name -eq 'Slow'){return $script:clock -ge [datetime]'2020-01-01T00:00:01'};$true}
function App($Name,$Enabled=$true){[pscustomobject]@{Name=$Name;Enabled=$Enabled;Kind='Command';FileName='C:\example.exe';WaitMode='Launch';TimeoutSeconds=2;DelaySeconds=0}}
function TestGroup($Name,$Apps,$Failure='Stop',$Enabled=$true){[pscustomobject]@{Id=$Name;Name=$Name;Apps=$Apps;Enabled=$Enabled;OnFailure=$Failure;DelayAfterSeconds=0}}
function Config($Groups){[pscustomobject]@{SchemaVersion=2;UserSid='test';TaskName='test';Groups=$Groups}}
$config=Config @((TestGroup 'First' @((App 'Slow'),(App 'Fast'),(App 'Disabled' $false))),(TestGroup 'Skipped' @((App 'Skipped')) 'Stop' $false),(TestGroup 'Second' @((App 'Later'))))
Test-StartupConfiguration $config
Invoke-StartupGroups $config
if($events.IndexOf('start:Fast') -gt $events.IndexOf('check:Slow')){throw 'Readiness waited before all TestGroup launches.'}
if($events.IndexOf('start:Later') -lt $events.IndexOf('log:Ready: Slow')){throw 'Next TestGroup launched before the slow app was ready.'}
if($events.Contains('start:Disabled') -or $events.Contains('start:Skipped')){throw 'Disabled apps or groups launched.'}
'PASS: TestGroup members launch before waits; next TestGroup waits for all; disabled members/groups are skipped.'
$script:events.Clear();$script:clock=[datetime]'2020-01-01'
$failed=$false
try{Invoke-StartupGroups (Config @((TestGroup 'Fail' @((App 'Never'))),(TestGroup 'Held' @((App 'Held')))))}catch{$failed=$true}
if(-not $failed -or $events.Contains('start:Held')){throw 'Timeout did not hold the next TestGroup.'}
'PASS: timed-out TestGroup prevents later groups.'
$script:events.Clear()
Invoke-StartupGroups (Config @((TestGroup 'Fail' @((App 'Broken')) 'Continue'),(TestGroup 'Following' @((App 'Following')))))
if(-not $events.Contains('start:Following')){throw 'Explicit continue policy did not continue.'}
'PASS: explicit continue policy releases the next TestGroup after failure.'
$invalid=Config @((TestGroup 'bad' @((App 'Example'))))
$invalid.Groups[0].Apps[0].WaitMode='NotAProbe'
try{Test-StartupConfiguration $invalid;throw 'Invalid config was accepted'}catch{if($_.Exception.Message -eq 'Invalid config was accepted'){throw}}
'PASS: invalid readiness mode is rejected before launches.'


