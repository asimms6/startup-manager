param([switch]$Detached)
$ErrorActionPreference='Stop'
$taskName='Startup Manager desktop launch'
if($Detached){
    try { Start-Process -FilePath (Join-Path $PSScriptRoot 'manager\StartupManager.exe') -ArgumentList '--desktop' -WorkingDirectory $PSScriptRoot }
    finally { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue }
    exit
}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$action=New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Detached')
$principal=New-ScheduledTaskPrincipal -UserId $identity.User.Value -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
