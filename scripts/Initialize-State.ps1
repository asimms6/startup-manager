$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'config.json')){exit}
$streamPath=Join-Path $env:ProgramFiles 'Elgato\StreamDeck\StreamDeck.exe'
foreach($candidate in @(Get-AvailableStartupApps -IncludePriority -IncludeDisabled)){
    if($candidate.App.Kind -eq 'Command' -and [IO.Path]::GetFileName($candidate.App.FileName) -eq 'StreamDeck.exe'){$streamPath=$candidate.App.FileName}
}
if(-not(Test-Path -LiteralPath $streamPath)){throw 'Install Stream Deck before setting up Startup Manager.'}
$wave=Get-AppxPackage -Name Elgato.WaveLink | Select-Object -First 1
if(-not $wave -or -not(Get-Service WavelinkSEService -ErrorAction SilentlyContinue)){throw 'This release requires Wave Link 3 and its Windows service. Install Wave Link 3 first.'}
[xml]$manifest=Get-Content -LiteralPath (Join-Path $wave.InstallLocation 'AppxManifest.xml')
$application=@($manifest.Package.Applications.Application)[0]
$config=[pscustomobject]@{UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;StreamDeckPath=$streamPath;WaveFamily=$wave.PackageFamilyName;WaveAppId=($wave.PackageFamilyName+'!'+$application.Id);Apps=@()}
$serviceKey=Get-Item -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\WavelinkSEService'
$failure=$serviceKey.GetValue('FailureActions')
$backup=[pscustomobject]@{Registry=@();Tasks=@();Service=[pscustomobject]@{FailureActionsExisted=($null -ne $failure);FailureActions=if($failure){[Convert]::ToBase64String($failure)}else{$null};Start=$serviceKey.GetValue('Start')}}
Write-StartupJson (Join-Path $PSScriptRoot 'backup.json') $backup
Write-StartupJson (Join-Path $PSScriptRoot 'config.json') $config
