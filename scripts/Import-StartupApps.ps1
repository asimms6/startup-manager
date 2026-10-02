$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
$resultPath=Join-Path $PSScriptRoot 'import-result.json'
$configPath=Join-Path $PSScriptRoot 'config.json'
$backupPath=Join-Path $PSScriptRoot 'backup.json'
$config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$backup=Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
$beforeConfig=Get-Content -LiteralPath $configPath -Raw
$beforeBackup=Get-Content -LiteralPath $backupPath -Raw
$changedRegistry=@();$changedTasks=@()
try{
    if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $config.UserSid){throw 'Use the Simms account for this change.'}
    if(-not(Get-ScheduledTask -TaskName 'Elgato Ordered Startup' -ErrorAction SilentlyContinue)){throw 'Install the ordered startup sequence before importing apps.'}
    $ids=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'pending-import.json') -Raw | ConvertFrom-Json
    $ids=@($ids)
    $available=@(Get-AvailableStartupApps)
    $selected=@($available | Where-Object {$_.Id -in $ids})
    if($selected.Count -ne $ids.Count){throw 'Startup settings changed since the list was opened. Refresh the list and try again.'}
    foreach($candidate in $selected){
        if($candidate.SourceKind -in 'Registry','Package'){
            $existedKey=Test-Path -LiteralPath $candidate.SourcePath
            $key=if($existedKey){Get-Item -LiteralPath $candidate.SourcePath}else{$null}
            $existed=$key -and $key.GetValueNames() -contains $candidate.SourceName
            $type=if($existed){[string]$key.GetValueKind($candidate.SourceName)}elseif($candidate.SourceKind -eq 'Package'){'DWord'}else{'Binary'}
            $value=if(-not $existed){$null}elseif($type -eq 'Binary'){[Convert]::ToBase64String($key.GetValue($candidate.SourceName))}else{$key.GetValue($candidate.SourceName)}
            $record=[pscustomobject]@{Path=$candidate.SourcePath;Name=$candidate.SourceName;Existed=[bool]$existed;Type=$type;Value=$value;Change=if($candidate.SourceKind -eq 'Package'){'DisablePackage'}else{'DisableRun'}}
            $changedRegistry+=$record
            if(-not @($backup.Registry | Where-Object {$_.Path -eq $record.Path -and $_.Name -eq $record.Name}).Count){$backup.Registry=@($backup.Registry)+$record}
        }else{
            $record=[pscustomobject]@{Name=$candidate.TaskName;Path=$candidate.TaskPath;Xml=(Export-ScheduledTask -TaskName $candidate.TaskName -TaskPath $candidate.TaskPath)}
            $changedTasks+=$record
            if(-not @($backup.Tasks | Where-Object {$_.Name -eq $record.Name -and $_.Path -eq $record.Path}).Count){$backup.Tasks=@($backup.Tasks)+$record}
        }
        $app=$candidate.App
        $app | Add-Member -NotePropertyName SourceId -NotePropertyValue $candidate.Id -Force
        $app | Add-Member -NotePropertyName Enabled -NotePropertyValue $true -Force
        # Replace re-enabled entries already controlled by this manager, without duplicating them.
        $existing=@($config.Apps | Where-Object {$_.SourceId -eq $candidate.Id -or $_.Name -eq $candidate.Name})
        if($existing.Count){$config.Apps=@($config.Apps | Where-Object {$_.SourceId -ne $candidate.Id -and $_.Name -ne $candidate.Name})}
        $config.Apps=@($config.Apps)+$app
    }
    Write-StartupJson $backupPath $backup
    foreach($record in $changedRegistry){
        if(-not(Test-Path -LiteralPath $record.Path)){New-Item -Path $record.Path -Force | Out-Null}
        if($record.Change -eq 'DisablePackage'){$value=1}else{$value=New-Object byte[] 12;$value[0]=3}
        New-ItemProperty -LiteralPath $record.Path -Name $record.Name -PropertyType $record.Type -Value $value -Force | Out-Null
    }
    foreach($record in $changedTasks){
        [xml]$xml=$record.Xml
        $ns=New-Object System.Xml.XmlNamespaceManager($xml.NameTable);$ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
        foreach($node in $xml.SelectNodes('/t:Task/t:Triggers/t:LogonTrigger',$ns)){
            $enabled=$node.SelectSingleNode('t:Enabled',$ns)
            if(-not $enabled){$enabled=$xml.CreateElement('Enabled',$ns.LookupNamespace('t'));$node.AppendChild($enabled) | Out-Null}
            $enabled.InnerText='false'
        }
        Register-ScheduledTask -TaskName $record.Name -TaskPath $record.Path -Xml $xml.OuterXml -Force | Out-Null
    }
    Write-StartupJson $configPath $config
    Write-StartupJson $resultPath ([pscustomobject]@{Success=$true;Count=$selected.Count})
}catch{
    $message=$_.Exception.Message
    foreach($record in $changedRegistry){
        try{
            if($record.Existed){$value=if($record.Type -eq 'Binary'){[Convert]::FromBase64String($record.Value)}else{[int]$record.Value};New-ItemProperty -LiteralPath $record.Path -Name $record.Name -PropertyType $record.Type -Value $value -Force | Out-Null}
            else{Remove-ItemProperty -LiteralPath $record.Path -Name $record.Name -ErrorAction SilentlyContinue}
        }catch{$message+='; registry restore failed: '+$_.Exception.Message}
    }
    foreach($record in $changedTasks){try{Register-ScheduledTask -TaskName $record.Name -TaskPath $record.Path -Xml $record.Xml -Force | Out-Null}catch{$message+='; task restore failed: '+$_.Exception.Message}}
    Set-Content -LiteralPath $configPath -Value $beforeConfig -Encoding UTF8
    Set-Content -LiteralPath $backupPath -Value $beforeBackup -Encoding UTF8
    Write-StartupJson $resultPath ([pscustomobject]@{Success=$false;Error=$message})
    exit 1
}
