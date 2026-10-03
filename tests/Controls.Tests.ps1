$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\StartupManager.Common.ps1')
$script:currentXml='<Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers><Actions><Exec><Command>C:\Updated\App.exe</Command></Exec></Actions></Task>'
$script:patched=$null
function Export-ScheduledTask {param($TaskName,$TaskPath);$script:currentXml}
function Register-ScheduledTask {param($TaskName,$TaskPath,$Xml,[switch]$Force);$script:patched=$Xml}
function Get-ScheduledTask {
    param($TaskName,$TaskPath,$ErrorAction)
    [xml]$xml=$script:patched
    [pscustomobject]@{Triggers=@([pscustomobject]@{CimClass=[pscustomobject]@{CimClassName='MSFT_TaskLogonTrigger'};Enabled=($xml.Task.Triggers.LogonTrigger.Enabled -eq 'true')})}
}
$backup=[pscustomobject]@{Registry=@();Tasks=@([pscustomobject]@{Name='Example';Path='\';Xml=($script:currentXml.Replace('Updated','Original'))})}
Disable-StartupControls $backup
[xml]$actual=$script:patched
if($actual.Task.Actions.Exec.Command -ne 'C:\Updated\App.exe' -or $actual.Task.Triggers.LogonTrigger.Enabled -ne 'false'){throw 'Repair changed current task actions or left its logon trigger enabled.'}
'PASS: repair disables logon while retaining an upgraded task action.'
