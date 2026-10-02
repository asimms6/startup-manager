param([switch]$Candidates)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
if($Candidates){
    . (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
    ConvertTo-Json -InputObject @(Get-AvailableStartupApps) -Depth 8 -Compress
}else{
    . (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
    $readiness=& (Join-Path $PSScriptRoot 'Start-OrderedApps.ps1') -CheckOnly
    $task=Get-ScheduledTask -TaskName 'Elgato Ordered Startup' -ErrorAction SilentlyContinue
    $backup=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup.json') -Raw | ConvertFrom-Json
    [pscustomobject]@{StreamDeckRunning=$readiness.StreamDeckRunning;WaveLinkReady=$readiness.WaveLinkReady;Service=$readiness.Service;Task=if($task){[string]$task.State}else{'Not installed'};Conflicts=@(Get-StartupConflicts $backup)} | ConvertTo-Json -Compress
}
