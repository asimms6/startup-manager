function Get-SessionProcesses([string]$Name) {
    @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object {$_.SessionId -eq (Get-Process -Id $PID).SessionId})
}
function Start-ManagedApp($App) {
    if($App.ServiceName){
        $timeout=if($App.TimeoutSeconds){[int]$App.TimeoutSeconds}else{60}
        $deadline=(Get-Date).AddSeconds($timeout)
        while((Get-Service -Name $App.ServiceName -ErrorAction SilentlyContinue).Status -ne 'Running'){
            if((Get-Date) -ge $deadline){throw ('Required service is not running: '+$App.ServiceName)}
            Start-Sleep -Milliseconds 250
        }
    }
    if($App.WaitMode -in 'Process','Responsive','PortFile' -and $App.ProcessName -and @(Get-SessionProcesses $App.ProcessName).Count){return [pscustomobject]@{Process=$null}}
    switch($App.Kind){
        'Command' {
            $info=New-Object System.Diagnostics.ProcessStartInfo
            $info.FileName=$App.FileName; $info.Arguments=$App.Arguments; $info.UseShellExecute=$true
            if($App.WorkingDirectory){$info.WorkingDirectory=$App.WorkingDirectory}
            $process=[System.Diagnostics.Process]::Start($info)
            return [pscustomobject]@{Process=$process}
        }
        'PackageApp' {Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList ('shell:AppsFolder\'+$App.AppId) -WindowStyle Hidden}
        'Task' {Start-ScheduledTask -TaskName $App.TaskName -TaskPath $App.TaskPath}
        default {throw ('Unknown app kind: '+$App.Kind)}
    }
    [pscustomobject]@{Process=$null}
}
function Test-ManagedAppReady($App,$Launch) {
    if($App.ServiceName -and (Get-Service -Name $App.ServiceName -ErrorAction SilentlyContinue).Status -ne 'Running'){return $false}
    $mode=if($App.WaitMode){$App.WaitMode}else{'Launch'}
    if($mode -eq 'Launch'){return $true}
    if($mode -eq 'Exit'){
        if(-not $Launch.Process){throw ('No process handle available for exit check: '+$App.Name)}
        $Launch.Process.Refresh()
        if(-not $Launch.Process.HasExited){return $false}
        if($Launch.Process.ExitCode -ne 0){throw ('App exited with code '+$Launch.Process.ExitCode+': '+$App.Name)}
        return $true
    }
    if(-not $App.ProcessName){throw ('A process name is required for '+$App.Name)}
    $processes=@(Get-SessionProcesses $App.ProcessName)
    if(-not $processes.Count){return $false}
    if($mode -eq 'Process'){return $true}
    if($mode -eq 'Responsive'){return @($processes | Where-Object {$_.Responding}).Count -gt 0}
    if($mode -eq 'PortFile'){
        try{
            $path=[Environment]::ExpandEnvironmentVariables($App.PortFile)
            $file=Get-Item -LiteralPath $path
            $start=($processes | Sort-Object StartTime | Select-Object -First 1).StartTime
            if($file.LastWriteTime -lt $start.AddSeconds(-2)){return $false}
            $port=[int](Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).port
            return @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Where-Object {$_.OwningProcess -in $processes.Id -or $_.OwningProcess -eq 4}).Count -gt 0
        }catch{return $false}
    }
    throw ('Unknown wait mode: '+$mode)
}
function Invoke-StartupGroups($Config) {
    foreach($group in $Config.Groups){
        if($group.Enabled -eq $false){continue}
        Write-StartupLog ('Group started: '+$group.Name)
        $pending=New-Object System.Collections.Generic.List[object]
        $failed=$false
        foreach($app in $group.Apps){
            if($app.Enabled -eq $false){continue}
            try{
                $launch=Start-ManagedApp $app
                $timeout=if($app.TimeoutSeconds){[int]$app.TimeoutSeconds}else{60}
                $pending.Add([pscustomobject]@{App=$app;Launch=$launch;Deadline=(Get-Date).AddSeconds($timeout);ReadySince=$null})
                Write-StartupLog ('Launch accepted: '+$app.Name)
            }catch{Write-StartupLog ('Launch failed: '+$app.Name+' — '+$_.Exception.Message);$failed=$true}
        }
        while($pending.Count){
            foreach($item in @($pending.ToArray())){
                try{
                    $ready=Test-ManagedAppReady $item.App $item.Launch
                    $now=Get-Date
                    if($ready){
                        if(-not $item.ReadySince){$item.ReadySince=$now}
                        $stable=if($item.App.StableSeconds){[double]$item.App.StableSeconds}else{0}
                        $delay=if($item.App.DelaySeconds){[double]$item.App.DelaySeconds}else{0}
                        if(($now-$item.ReadySince).TotalSeconds -ge ($stable+$delay)){
                            Write-StartupLog ('Ready: '+$item.App.Name);[void]$pending.Remove($item);continue
                        }
                    }else{$item.ReadySince=$null}
                    if($now -ge $item.Deadline){throw ('Timed out waiting for '+$item.App.Name)}
                }catch{Write-StartupLog $_.Exception.Message;$failed=$true;[void]$pending.Remove($item)}
            }
            if($pending.Count){Start-Sleep -Milliseconds 250}
        }
        if($failed -and $group.OnFailure -ne 'Continue'){throw ('Group failed: '+$group.Name+'. Later groups were held.')}
        if($group.DelayAfterSeconds -gt 0){Start-Sleep -Milliseconds ([int]([double]$group.DelayAfterSeconds*1000))}
        Write-StartupLog ('Group complete: '+$group.Name)
    }
}

