param([switch]$Candidates)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
if($Candidates){ConvertTo-Json -InputObject @(Get-AvailableStartupApps) -Depth 8 -Compress;exit}
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
$backup=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup.json') -Raw | ConvertFrom-Json
$task=Get-ScheduledTask -TaskName $config.TaskName -ErrorAction SilentlyContinue
$last=if($task){Get-ScheduledTaskInfo -TaskName $config.TaskName}else{$null}
[pscustomobject]@{Task=if($task){[string]$task.State}else{'Not installed'};LastResult=if($last){$last.LastTaskResult}else{$null};Groups=@($config.Groups).Count;Apps=@($config.Groups | ForEach-Object {$_.Apps}).Count;Conflicts=@(Get-StartupConflicts $backup)} | ConvertTo-Json -Compress

