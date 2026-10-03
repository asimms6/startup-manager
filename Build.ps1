param([switch]$UpdateInstalled,[switch]$Package)
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
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'scripts') -Filter '*.ps1'){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $runtime $file.Name) -Force}
    Write-Output 'Updated the installed app and scripts; configuration and backups were preserved.'
}
if($Package){
    $release=Join-Path $PSScriptRoot ('release\StartupManager-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    & dotnet publish (Join-Path $PSScriptRoot 'StartupManager.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o (Join-Path $release 'manager') --nologo
    if($LASTEXITCODE -ne 0){throw 'Portable build failed.'}
    Copy-Item -Path (Join-Path $PSScriptRoot 'scripts\*.ps1') -Destination $release
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $release
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.schema.json') -Destination $release
    Compress-Archive -LiteralPath $release -DestinationPath ($release+'.zip')
    Write-Output ('Shareable package: '+$release+'.zip')
}
