$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this change as administrator.'}
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
Test-StartupConfiguration $config
if($identity.User.Value -ne $config.UserSid){throw 'Use the account that created this configuration.'}
$backup=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup.json') -Raw | ConvertFrom-Json
try{
    $apps=@($config.Groups | ForEach-Object {$_.Apps})
    foreach($candidate in @(Get-AvailableStartupApps -IncludeDisabled)){
        $owned=@($apps | Where-Object {
            $_.SourceId -eq $candidate.Id -or
            ($_.Kind -eq 'Command' -and $candidate.App.Kind -eq 'Command' -and $_.FileName -eq $candidate.App.FileName) -or
            ($_.Kind -eq 'PackageApp' -and $_.AppId -eq $candidate.App.AppId) -or
            ($_.Kind -eq 'Task' -and $_.TaskName -eq $candidate.TaskName -and $_.TaskPath -eq $candidate.TaskPath)
        })
        if($owned.Count){Save-StartupCandidate $backup $candidate}
    }
    Write-StartupJson (Join-Path $PSScriptRoot 'backup.json') $backup
    $action=New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot 'Start-OrderedApps.ps1')+'"')
    $trigger=New-ScheduledTaskTrigger -AtLogOn -User $identity.User.Value
    $principal=New-ScheduledTaskPrincipal -UserId $identity.User.Value -LogonType Interactive -RunLevel Limited
    $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 24) -StartWhenAvailable
    Register-ScheduledTask -TaskName $config.TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Launch user-defined startup groups in order, waiting for each group before starting the next.' -Force | Out-Null
    Disable-StartupControls $backup
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) 'Startup Manager.lnk'
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath=Join-Path $PSScriptRoot 'manager\StartupManager.exe'
    $shortcut.WorkingDirectory=$PSScriptRoot
    $shortcut.Description='Manage ordered Windows startup groups'
    $shortcut.Save()
    Write-StartupJson (Join-Path $PSScriptRoot 'install-status.json') ([pscustomobject]@{Success=$true;Time=(Get-Date).ToString('o');Task=$config.TaskName;VerifiedFlags=$backup.Registry.Count})
}catch{
    $failure=$_.Exception.Message
    try{& (Join-Path $PSScriptRoot 'Restore-StartupOrder.ps1')}catch{$failure+='; rollback: '+$_.Exception.Message}
    Write-StartupJson (Join-Path $PSScriptRoot 'install-status.json') ([pscustomobject]@{Success=$false;Error=$failure})
    throw $failure
}

