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
function Get-AvailableStartupApps {
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
            if($name -in @('SecurityHealth','RtkAudUService','Stream Deck','Elgato Ordered Startup')){continue}
            if((Get-ApprovedState $definition.Approved $name) -notin 2,6){continue}
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
            if((Get-ApprovedState $approved $file.Name) -notin 2,6){continue}
            [pscustomobject]@{Id=($approved+'|'+$file.Name);Name=$file.BaseName;Origin='Startup folder';SourceKind='Registry';SourcePath=$approved;SourceName=$file.Name;App=[pscustomobject]@{Name=$file.BaseName;Kind='Command';FileName=$file.FullName;Arguments='';WorkingDirectory=$folder}}
        }
    }
    $base='HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData'
    foreach($package in Get-AppxPackage | Where-Object {-not $_.IsFramework -and $_.InstallLocation}){
        if($package.Name -eq 'Elgato.WaveLink'){continue}
        $familyPath=Join-Path $base $package.PackageFamilyName
        if(-not(Test-Path -LiteralPath $familyPath)){continue}
        try{
            [xml]$manifest=Get-Content -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -ErrorAction Stop
            foreach($application in $manifest.Package.Applications.Application){
                foreach($extension in $application.SelectNodes(".//*[local-name()='Extension' and @Category='windows.startupTask']")){
                    $startup=$extension.SelectSingleNode("./*[local-name()='StartupTask']")
                    if(-not $startup){continue}
                    $path=Join-Path $familyPath $startup.TaskId
                    if(-not(Test-Path -LiteralPath $path) -or (Get-Item -LiteralPath $path).GetValue('State') -ne 2){continue}
                    $name=$package.Name+' / '+$startup.TaskId
                    [pscustomobject]@{Id=($path+'|State');Name=$name;Origin='Packaged startup app';SourceKind='Package';SourcePath=$path;SourceName='State';App=[pscustomobject]@{Name=$name;Kind='PackageApp';AppId=($package.PackageFamilyName+'!'+$application.Id)}}
                }
            }
        }catch{continue}
    }
    foreach($task in Get-ScheduledTask | Where-Object {$_.TaskPath -notlike '\Microsoft\*' -and $_.TaskName -ne 'Elgato Ordered Startup' -and $_.State -ne 'Disabled'}){
        $logons=@($task.Triggers | Where-Object {$_.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger' -and $_.Enabled})
        if(-not $logons.Count -or -not @($task.Actions | Where-Object {$_.Execute}).Count){continue}
        if($task.TaskName -match 'Update|Telemetry|Metrics|Security|Defender'){continue}
        $name=$task.TaskPath+$task.TaskName
        [pscustomobject]@{Id=('Task|'+$name);Name=$name;Origin='Logon task';SourceKind='Task';TaskName=$task.TaskName;TaskPath=$task.TaskPath;App=[pscustomobject]@{Name=$name;Kind='Task';TaskName=$task.TaskName;TaskPath=$task.TaskPath}}
    }
}
function Write-StartupJson([string]$Path,$Value) {
    $temporary=$Path+'.tmp'
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}
