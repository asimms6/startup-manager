param([switch]$UpdateInstalled)
$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot '.runtime'
if($UpdateInstalled){
    if(-not(Test-Path -LiteralPath (Join-Path $runtime 'config.json'))){throw 'The startup sequence has not been installed on this PC.'}
    $exe=Join-Path $runtime 'manager\StartupManager.exe'
    if(@(Get-Process -Name StartupManager -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}).Count){throw 'Close Startup Manager before updating it.'}
}
& dotnet publish (Join-Path $PSScriptRoot 'StartupManager.csproj') -c Release --self-contained false -o (Join-Path $PSScriptRoot 'publish') --nologo
if($LASTEXITCODE -ne 0){throw 'Build failed.'}
if($UpdateInstalled){
    Copy-Item -Path (Join-Path $PSScriptRoot 'publish\*') -Destination (Join-Path $runtime 'manager') -Force
    foreach($name in @('Start-OrderedApps.ps1','StartupManager.Common.ps1','Export-StartupState.ps1','Import-StartupApps.ps1','Restore-StartupOrder.ps1','Install-StartupOrder.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('scripts\'+$name)) -Destination (Join-Path $runtime $name) -Force}
    Write-Output 'Updated the installed app and scripts; configuration and backups were preserved.'
}
