$ErrorActionPreference = 'Stop'
$runtime = $PSScriptRoot
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
$taskName = 'Elgato Ordered Startup'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this installer as administrator.' }
$sourceConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
if ($identity.User.Value -ne $sourceConfig.UserSid) { throw 'Use the account that created this configuration; do not elevate under a different account.' }
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$statusPath = Join-Path $runtime 'install-status.json'
try {
    foreach ($name in @('Start-OrderedApps.ps1','Restore-StartupOrder.ps1','StartupManager.Common.ps1','Export-StartupState.ps1','Import-StartupApps.ps1','config.json','backup.json')) {
        $source=Join-Path $PSScriptRoot $name
        $destination=Join-Path $runtime $name
        if(-not [IO.Path]::GetFullPath($source).Equals([IO.Path]::GetFullPath($destination),[StringComparison]::OrdinalIgnoreCase)) { Copy-Item -LiteralPath $source -Destination $destination -Force }
    }
    $backup = Get-Content -LiteralPath (Join-Path $runtime 'backup.json') -Raw | ConvertFrom-Json
    # Capture priority autoruns on this computer; never distribute another user's backup.
    foreach($candidate in @(Get-AvailableStartupApps -IncludePriority -IncludeDisabled)){
        $priority=($candidate.App.Kind -eq 'Command' -and [IO.Path]::GetFileName($candidate.App.FileName) -eq 'StreamDeck.exe') -or ($candidate.SourceKind -eq 'Package' -and $candidate.App.AppId -like 'Elgato.WaveLink_*!*')
        if(-not $priority -or @($backup.Registry | Where-Object {$_.Path -eq $candidate.SourcePath -and $_.Name -eq $candidate.SourceName}).Count){continue}
        $key=Get-Item -LiteralPath $candidate.SourcePath -ErrorAction SilentlyContinue
        $existed=$key -and $key.GetValueNames() -contains $candidate.SourceName
        $type=if($candidate.SourceKind -eq 'Package'){'DWord'}else{'Binary'}
        $value=if(-not $existed){$null}elseif($type -eq 'Binary'){[Convert]::ToBase64String($key.GetValue($candidate.SourceName))}else{$key.GetValue($candidate.SourceName)}
        $backup.Registry=@($backup.Registry)+[pscustomobject]@{Path=$candidate.SourcePath;Name=$candidate.SourceName;Existed=[bool]$existed;Type=$type;Value=$value;Change=if($type -eq 'Binary'){'DisableRun'}else{'DisablePackage'}}
    }
    Write-StartupJson (Join-Path $runtime 'backup.json') $backup
    $action = New-ScheduledTaskAction -Execute "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $runtime 'Start-OrderedApps.ps1') + '"')
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.User.Value
    $principal = New-ScheduledTaskPrincipal -UserId $identity.User.Value -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 10) -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Stream Deck first, Wave Link 3 second, then the existing enabled desktop startup apps.' -Force -ErrorAction Stop | Out-Null
    foreach ($entry in $backup.Registry) {
        if ($entry.Change -eq 'DisableRun') {
            $bytes = New-Object byte[] 12
            $bytes[0] = 3
            if (-not (Test-Path -LiteralPath $entry.Path)) { New-Item -Path $entry.Path | Out-Null }
            New-ItemProperty -LiteralPath $entry.Path -Name $entry.Name -PropertyType Binary -Value $bytes -Force | Out-Null
        } elseif ($entry.Change -eq 'DisablePackage') {
            if (-not (Test-Path -LiteralPath $entry.Path)) { New-Item -Path $entry.Path | Out-Null }
            Set-ItemProperty -LiteralPath $entry.Path -Name $entry.Name -Value 1 -Type DWord
        } elseif ($entry.Change -eq 'Preserve') {
            New-ItemProperty -LiteralPath $entry.Path -Name $entry.Name -PropertyType $entry.Type -Value ([Convert]::FromBase64String($entry.Value)) -Force | Out-Null
        }
    }
    foreach ($task in $backup.Tasks) {
        [xml]$xml = $task.Xml
        $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
        $ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
        foreach ($triggerNode in $xml.SelectNodes('/t:Task/t:Triggers/t:LogonTrigger',$ns)) {
            $enabled = $triggerNode.SelectSingleNode('t:Enabled',$ns)
            if (-not $enabled) { $enabled=$xml.CreateElement('Enabled',$ns.LookupNamespace('t')); $triggerNode.AppendChild($enabled) | Out-Null }
            $enabled.InnerText = 'false'
        }
        Register-ScheduledTask -TaskName $task.Name -TaskPath $task.Path -Xml $xml.OuterXml -Force | Out-Null
    }
    Set-Service -Name WavelinkSEService -StartupType Automatic
    & "$env:WINDIR\System32\sc.exe" failure WavelinkSEService reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set Wave Link service recovery.' }
    Start-Service -Name WavelinkSEService
    (Get-Service WavelinkSEService).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    foreach ($entry in $backup.Registry) {
        $value=(Get-Item -LiteralPath $entry.Path).GetValue($entry.Name)
        $actual=if($value -is [byte[]]){[int]$value[0]}else{[int]$value}
        $expected=if($entry.Change -eq 'DisableRun'){3}elseif($entry.Change -eq 'DisablePackage'){1}else{[int]([Convert]::FromBase64String($entry.Value))[0]}
        if($actual -ne $expected){throw ('Startup verification failed: '+$entry.Name)}
    }
    if(@(Get-StartupConflicts $backup).Count){throw 'Independent startup entries remain enabled.'}
    $shortcutPath=Join-Path ([Environment]::GetFolderPath('Programs')) 'Startup Manager.lnk'
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath=Join-Path $runtime 'manager\StartupManager.exe'
    $shortcut.WorkingDirectory=Join-Path $runtime 'manager'
    $shortcut.Description='Manage Stream Deck, Wave Link, and ordered startup apps'
    $shortcut.Save()
    [pscustomobject]@{Success=$true;Time=(Get-Date).ToString('o');Runtime=$runtime;Task=$taskName;Service=[string](Get-Service WavelinkSEService).Status;VerifiedFlags=$backup.Registry.Count;Shortcut=$shortcutPath} | ConvertTo-Json | Set-Content -LiteralPath $statusPath
} catch {
    $failure = $_.Exception.Message
    try { & (Join-Path $runtime 'Restore-StartupOrder.ps1') } catch { $failure += '; rollback: ' + $_.Exception.Message }
    [pscustomobject]@{Success=$false;Time=(Get-Date).ToString('o');Error=$failure} | ConvertTo-Json | Set-Content -LiteralPath $statusPath
    throw $failure
}
