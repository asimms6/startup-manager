function Split-StartupCommand([string]$Command) {
    $expanded=[Environment]::ExpandEnvironmentVariables($Command)
    if($expanded -match '^"([^"]+)"\s*(.*)$'){ $file=$matches[1];$arguments=$matches[2] }
    elseif($expanded -match '^(.+?\.exe)\s*(.*)$'){ $file=$matches[1];$arguments=$matches[2] }
    else { return $null }
    [pscustomobject]@{FileName=$file;Arguments=$arguments;WorkingDirectory=(Split-Path -Parent $file)}
}
function Get-ApprovedState([string]$Path,[string]$Name) {
    if(-not(Test-Path -LiteralPath $Path)){return 2}
    $value=(Get-Item -LiteralPath $Path).GetValue($Name)
    if($null -eq $value){return 2}
    [int]$value[0]
}
function Get-AvailableStartupApps([switch]$IncludePriority,[switch]$IncludeDisabled) {
    $definitions=@(
        @{Run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';Approved='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';Label='User startup'},
        @{Run='HKLM:\Software\Microsoft\Windows\CurrentVersion\Run';Approved='HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';Label='Machine startup'},
        @{Run='HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run';Approved='HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32';Label='Machine startup (32-bit)'},
        @{Run='HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run';Approved='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32';Label='User startup (32-bit)'}
    )
    foreach($definition in $definitions){
        if(-not(Test-Path -LiteralPath $definition.Run)){continue}
        $key=Get-Item -LiteralPath $definition.Run
        foreach($name in $key.GetValueNames()){
            if($name -in @('SecurityHealth','RtkAudUService','Elgato Ordered Startup')){continue}
            if(-not $IncludeDisabled -and (Get-ApprovedState $definition.Approved $name) -notin 2,6){continue}
            $command=[string]$key.GetValue($name)
            if(-not $command.Trim()){continue}
            $parsed=Split-StartupCommand $command
            if(-not $parsed){continue}
            [pscustomobject]@{Id=($definition.Approved+'|'+$name);Name=$name;Origin=$definition.Label;SourceKind='Registry';SourcePath=$definition.Approved;SourceName=$name;RunPath=$definition.Run;Command=$command;App=[pscustomobject]@{Name=$name;Kind='Command';FileName=$parsed.FileName;Arguments=$parsed.Arguments;WorkingDirectory=$parsed.WorkingDirectory}}
        }
    }
    foreach($folder in @([Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup'))){
        if(-not(Test-Path -LiteralPath $folder)){continue}
        $approved=if($folder -eq [Environment]::GetFolderPath('Startup')){'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'}else{'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'}
        foreach($file in Get-ChildItem -LiteralPath $folder -File | Where-Object {$_.Extension -in '.lnk','.exe','.cmd','.bat'}){
            if(-not $IncludeDisabled -and (Get-ApprovedState $approved $file.Name) -notin 2,6){continue}
            [pscustomobject]@{Id=($approved+'|'+$file.Name);Name=$file.BaseName;Origin='Startup folder';SourceKind='Registry';SourcePath=$approved;SourceName=$file.Name;App=[pscustomobject]@{Name=$file.BaseName;Kind='Command';FileName=$file.FullName;Arguments='';WorkingDirectory=$folder}}
        }
    }
    $base='HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData'
    foreach($package in Get-AppxPackage | Where-Object {-not $_.IsFramework -and $_.InstallLocation}){
        $familyPath=Join-Path $base $package.PackageFamilyName
        if(-not(Test-Path -LiteralPath $familyPath)){continue}
        try{
            [xml]$manifest=Get-Content -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -ErrorAction Stop
            foreach($application in $manifest.Package.Applications.Application){
                foreach($extension in $application.SelectNodes(".//*[local-name()='Extension' and @Category='windows.startupTask']")){
                    $startup=$extension.SelectSingleNode("./*[local-name()='StartupTask']")
                    if(-not $startup){continue}
                    $path=Join-Path $familyPath $startup.TaskId
                    if(-not(Test-Path -LiteralPath $path)){continue}
                    if(-not $IncludeDisabled -and (Get-Item -LiteralPath $path).GetValue('State') -ne 2){continue}
                    $name=$package.Name+' / '+$startup.TaskId
                    [pscustomobject]@{Id=($path+'|State');Name=$name;Origin='Packaged startup app';SourceKind='Package';SourcePath=$path;SourceName='State';App=[pscustomobject]@{Name=$name;Kind='PackageApp';AppId=($package.PackageFamilyName+'!'+$application.Id)}}
                }
            }
        }catch{continue}
    }
    foreach($task in Get-ScheduledTask | Where-Object {$_.TaskPath -notlike '\Microsoft\*' -and $_.TaskName -ne 'Elgato Ordered Startup' -and $_.TaskName -notlike 'Startup Manager*' -and $_.State -ne 'Disabled'}){
        $logons=@($task.Triggers | Where-Object {$_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and $_.Enabled})
        if(-not $logons.Count -or -not @($task.Actions | Where-Object {$_.Execute}).Count){continue}
        if($task.TaskName -match 'Update|Telemetry|Metrics|Security|Defender'){continue}
        $name=$task.TaskPath+$task.TaskName
        [pscustomobject]@{Id=('Task|'+$name);Name=$name;Origin='Logon task';SourceKind='Task';TaskName=$task.TaskName;TaskPath=$task.TaskPath;App=[pscustomobject]@{Name=$name;Kind='Task';TaskName=$task.TaskName;TaskPath=$task.TaskPath}}
    }
}
function Get-StartupConflicts($Backup) {
    foreach($entry in $Backup.Registry){
        if($entry.Change -eq 'DisableRun' -and (Get-ApprovedState $entry.Path $entry.Name) -in 2,6){$entry.Name}
        elseif($entry.Change -eq 'DisablePackage' -and (Test-Path -LiteralPath $entry.Path) -and (Get-Item -LiteralPath $entry.Path).GetValue($entry.Name) -eq 2){Split-Path -Leaf (Split-Path -Parent $entry.Path)}
    }
    foreach($entry in $Backup.Tasks){
        $task=Get-ScheduledTask -TaskName $entry.Name -TaskPath $entry.Path -ErrorAction SilentlyContinue
        if($task -and @($task.Triggers | Where-Object {$_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and $_.Enabled}).Count){$entry.Name}
    }
}
function Write-StartupJson([string]$Path,$Value) {
    $temporary=$Path+'.tmp'
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
function Test-StartupConfiguration($Config) {
    if($Config.SchemaVersion -ne 2 -or -not $Config.UserSid -or -not $Config.TaskName -or -not $Config.PSObject.Properties['Groups']){throw 'Invalid startup group configuration.'}
    $ids=New-Object 'System.Collections.Generic.HashSet[string]'
    $sources=New-Object 'System.Collections.Generic.HashSet[string]'
    foreach($group in $Config.Groups){
        if(-not $group.Id -or -not $ids.Add([string]$group.Id) -or -not $group.Name -or -not $group.PSObject.Properties['Apps']){throw 'Group names, unique IDs, and app lists are required.'}
        if($group.OnFailure -notin 'Stop','Continue'){throw 'Group failure handling must be Stop or Continue.'}
        if([double]$group.DelayAfterSeconds -lt 0 -or [double]$group.DelayAfterSeconds -gt 3600){throw 'Group delays must be between 0 and 3600 seconds.'}
        foreach($app in $group.Apps){
            if(-not $app.Name -or $app.Kind -notin 'Command','PackageApp','Task'){throw 'Invalid app name or launch type.'}
            if($app.Kind -eq 'Command' -and -not [IO.Path]::IsPathRooted($app.FileName)){throw 'App paths must be absolute.'}
            if($app.Kind -eq 'PackageApp' -and -not $app.AppId){throw 'Packaged apps need an application ID.'}
            if($app.Kind -eq 'Task' -and (-not $app.TaskName -or -not $app.TaskPath)){throw 'Scheduled apps need a task name and path.'}
            $mode=if($app.WaitMode){$app.WaitMode}else{'Launch'}
            if($mode -notin 'Launch','Process','Responsive','Exit','PortFile'){throw 'Unknown app readiness check.'}
            if($mode -in 'Process','Responsive','PortFile' -and -not $app.ProcessName){throw 'Process-based checks require a process name.'}
            if($mode -eq 'PortFile' -and -not $app.PortFile){throw 'A local server check requires a JSON port file.'}
            if($mode -eq 'Exit' -and $app.Kind -ne 'Command'){throw 'Exit checks require an executable.'}
            foreach($field in 'TimeoutSeconds','StableSeconds','DelaySeconds'){
                if($app.PSObject.Properties[$field]){
                    $number=[double]$app.$field
                    if([double]::IsNaN($number) -or $number -lt 0 -or $number -gt 3600 -or ($field -eq 'TimeoutSeconds' -and $number -lt 1)){throw ('Invalid timing setting: '+$field)}
                }
            }
            if($app.SourceId -and -not $sources.Add([string]$app.SourceId)){throw 'A startup entry cannot belong to multiple groups.'}
        }
    }
}
function Save-StartupCandidate($Backup,$Candidate) {
    if($Candidate.SourceKind -in 'Registry','Package'){
        if(@($Backup.Registry | Where-Object {$_.Path -eq $Candidate.SourcePath -and $_.Name -eq $Candidate.SourceName}).Count){return}
        $key=Get-Item -LiteralPath $Candidate.SourcePath -ErrorAction SilentlyContinue
        $existed=$key -and $key.GetValueNames() -contains $Candidate.SourceName
        $type=if($Candidate.SourceKind -eq 'Package'){'DWord'}else{'Binary'}
        $value=if(-not $existed){$null}elseif($type -eq 'Binary'){[Convert]::ToBase64String($key.GetValue($Candidate.SourceName))}else{$key.GetValue($Candidate.SourceName)}
        $Backup.Registry=@($Backup.Registry)+[pscustomobject]@{Path=$Candidate.SourcePath;Name=$Candidate.SourceName;Existed=[bool]$existed;Type=$type;Value=$value;Change=if($type -eq 'Binary'){'DisableRun'}else{'DisablePackage'}}
    }elseif(-not @($Backup.Tasks | Where-Object {$_.Name -eq $Candidate.TaskName -and $_.Path -eq $Candidate.TaskPath}).Count){
        $Backup.Tasks=@($Backup.Tasks)+[pscustomobject]@{Name=$Candidate.TaskName;Path=$Candidate.TaskPath;Xml=(Export-ScheduledTask -TaskName $Candidate.TaskName -TaskPath $Candidate.TaskPath)}
    }
}
function Disable-StartupControls($Backup) {
    foreach($entry in $Backup.Registry){
        if($entry.Change -notin 'DisableRun','DisablePackage'){continue}
        if(-not(Test-Path -LiteralPath $entry.Path)){New-Item -Path $entry.Path | Out-Null}
        if($entry.Change -eq 'DisableRun'){$value=New-Object byte[] 12;$value[0]=3;$type='Binary'}else{$value=1;$type='DWord'}
        New-ItemProperty -LiteralPath $entry.Path -Name $entry.Name -PropertyType $type -Value $value -Force | Out-Null
        $actual=(Get-Item -LiteralPath $entry.Path).GetValue($entry.Name)
        if(($type -eq 'Binary' -and $actual[0] -ne 3) -or ($type -eq 'DWord' -and $actual -ne 1)){throw ('Could not disable startup entry: '+$entry.Name)}
    }
    foreach($record in $Backup.Tasks){
        [xml]$xml=Export-ScheduledTask -TaskName $record.Name -TaskPath $record.Path
        $ns=New-Object System.Xml.XmlNamespaceManager($xml.NameTable);$ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
        foreach($node in $xml.SelectNodes('/t:Task/t:Triggers/t:LogonTrigger',$ns)){
            $enabled=$node.SelectSingleNode('t:Enabled',$ns)
            if(-not $enabled){$enabled=$xml.CreateElement('Enabled',$ns.LookupNamespace('t'));$node.AppendChild($enabled)|Out-Null}
            $enabled.InnerText='false'
        }
        Register-ScheduledTask -TaskName $record.Name -TaskPath $record.Path -Xml $xml.OuterXml -Force | Out-Null
    }
    if(@(Get-StartupConflicts $Backup).Count){throw 'Independent startup entries remain enabled.'}
}
