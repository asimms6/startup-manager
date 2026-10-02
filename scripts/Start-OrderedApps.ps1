param([switch]$CheckOnly, [switch]$PriorityOnly)
$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
$waveFamily=if($config.WaveFamily){$config.WaveFamily}else{'Elgato.WaveLink_g54w8ztgkx496'}
$waveAppId=if($config.WaveAppId){$config.WaveAppId}else{($waveFamily+'!App')}
$logPath = Join-Path $PSScriptRoot 'startup.log'
function Write-StartupLog([string]$Message) { Add-Content -LiteralPath $logPath -Value ('{0:o} {1}' -f (Get-Date), $Message) }
function Get-SessionProcess([string]$Name) {
    @(Get-Process -Name $Name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId })
}
function Test-WaveReady {
    if ((Get-Service -Name WavelinkSEService).Status -ne 'Running') { return $false }
    $processes = @(Get-SessionProcess 'Elgato.WaveLink')
    if (-not $processes.Count) { return $false }
    $infoPath = Join-Path $env:LOCALAPPDATA ('Packages\'+$waveFamily+'\LocalState\ws-info.json')
    try {
        $infoFile = Get-Item -LiteralPath $infoPath
        $processStart = ($processes | Sort-Object StartTime | Select-Object -First 1).StartTime
        if ($infoFile.LastWriteTime -lt $processStart.AddSeconds(-2)) { return $false }
        $port = [int](Get-Content -LiteralPath $infoPath -Raw | ConvertFrom-Json).port
        # Wave Link uses HTTP.sys, whose listening sockets belong to System (PID 4).
        # Require fresh port discovery from this launch plus a running app/service.
        $listener = @(Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -in $processes.Id -or $_.OwningProcess -eq 4 })
        return $listener.Count -gt 0
    } catch { return $false }
}
if ($CheckOnly) {
    [pscustomobject]@{StreamDeckRunning=(@(Get-SessionProcess 'StreamDeck').Count -gt 0);WaveLinkReady=(Test-WaveReady);Service=[string](Get-Service WavelinkSEService).Status}
    exit
}
$mutex = New-Object System.Threading.Mutex($false, 'Local\ElgatoOrderedStartup')
$locked = $false
try {
    $locked = $mutex.WaitOne(0)
    if (-not $locked) { exit }
    Write-StartupLog 'Sequence started.'
    if (-not @(Get-SessionProcess 'StreamDeck').Count) {
        Start-Process -FilePath $config.StreamDeckPath -ArgumentList '--runinbk' -WindowStyle Hidden
        Write-StartupLog 'Stream Deck launch requested.'
    }
    $deadline = (Get-Date).AddSeconds(60)
    $stable = 0
    while ((Get-Date) -lt $deadline -and $stable -lt 5) {
        $p = @(Get-SessionProcess 'StreamDeck' | Where-Object { $_.Responding })
        if ($p.Count) { $stable++ } else { $stable = 0 }
        Start-Sleep -Seconds 1
    }
    if ($stable -lt 5) { throw 'Stream Deck did not remain responsive. Other startup apps were held.' }
    Start-Sleep -Seconds 3
    Write-StartupLog 'Stream Deck responsive; initialization grace period complete.'
    $serviceDeadline = (Get-Date).AddSeconds(40)
    while ((Get-Service WavelinkSEService).Status -ne 'Running' -and (Get-Date) -lt $serviceDeadline) { Start-Sleep -Seconds 2 }
    if ((Get-Service WavelinkSEService).Status -ne 'Running') { throw 'Wave Link service is stopped. Other startup apps were held.' }
    if (-not @(Get-SessionProcess 'Elgato.WaveLink').Count) {
        Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList ('shell:AppsFolder\'+$waveAppId) -WindowStyle Hidden
        Write-StartupLog 'Wave Link 3 launch requested.'
    }
    $deadline = (Get-Date).AddSeconds(90)
    $stable = 0
    while ((Get-Date) -lt $deadline -and $stable -lt 3) {
        if (Test-WaveReady) { $stable++ } else { $stable = 0 }
        Start-Sleep -Seconds 2
    }
    if ($stable -lt 3) { throw 'Wave Link did not establish its local server. Other startup apps were held.' }
    Start-Sleep -Seconds 3
    Write-StartupLog 'Wave Link service and local server ready.'
    if ($PriorityOnly) { Write-StartupLog 'Priority-only validation complete.'; exit }
    foreach ($app in $config.Apps) {
        if ($app.PSObject.Properties['Enabled'] -and $app.Enabled -eq $false) { continue }
        try {
            switch ($app.Kind) {
                'Command' {
                    $psi = New-Object System.Diagnostics.ProcessStartInfo
                    $psi.FileName = $app.FileName
                    $psi.Arguments = $app.Arguments
                    $psi.UseShellExecute = $true
                    if ($app.WorkingDirectory) { $psi.WorkingDirectory = $app.WorkingDirectory }
                    [System.Diagnostics.Process]::Start($psi) | Out-Null
                }
                'PackageApp' {
                    Start-Process -FilePath "$env:WINDIR\explorer.exe" -ArgumentList ('shell:AppsFolder\' + $app.AppId) -WindowStyle Hidden
                }
                'Task' { Start-ScheduledTask -TaskName $app.TaskName -TaskPath $app.TaskPath }
            }
            Write-StartupLog ('Released: ' + $app.Name)
        } catch { Write-StartupLog ('Could not launch ' + $app.Name + ': ' + $_.Exception.Message) }
        Start-Sleep -Milliseconds 750
    }
    Write-StartupLog 'Sequence complete.'
} catch {
    Write-StartupLog ('ERROR: ' + $_.Exception.Message)
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
