param([switch]$Candidates)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
if($Candidates){
    . (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
    ConvertTo-Json -InputObject @(Get-AvailableStartupApps) -Depth 8 -Compress
}else{
    $readiness=& (Join-Path $PSScriptRoot 'Start-OrderedApps.ps1') -CheckOnly
    $task=Get-ScheduledTask -TaskName 'Elgato Ordered Startup' -ErrorAction SilentlyContinue
    [pscustomobject]@{StreamDeckRunning=$readiness.StreamDeckRunning;WaveLinkReady=$readiness.WaveLinkReady;Service=$readiness.Service;Task=if($task){[string]$task.State}else{'Not installed'}} | ConvertTo-Json -Compress
}
