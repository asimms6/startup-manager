$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
$configPath=Join-Path $PSScriptRoot 'config.json'
if(Test-Path -LiteralPath $configPath){
    $existing=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if($existing.UserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Use a fresh release folder; this configuration belongs to another account.'}
    if($existing.SchemaVersion -eq 2){Test-StartupConfiguration $existing;exit}
    Copy-Item -LiteralPath $configPath -Destination (Join-Path $PSScriptRoot 'config.v1.json') -Force
    $stream=[pscustomobject]@{Name='Stream Deck';Kind='Command';FileName=$existing.StreamDeckPath;Arguments='--runinbk';WorkingDirectory=(Split-Path -Parent $existing.StreamDeckPath);Enabled=$true;WaitMode='Responsive';ProcessName='StreamDeck';TimeoutSeconds=60;StableSeconds=5;DelaySeconds=3}
    $family=if($existing.WaveFamily){$existing.WaveFamily}else{'Elgato.WaveLink_g54w8ztgkx496'}
    $waveId=if($existing.WaveAppId){$existing.WaveAppId}else{($family+'!App')}
    $wave=[pscustomobject]@{Name='Wave Link';Kind='PackageApp';AppId=$waveId;Enabled=$true;WaitMode='PortFile';ProcessName='Elgato.WaveLink';ServiceName='WavelinkSEService';PortFile=('%LOCALAPPDATA%\Packages\'+$family+'\LocalState\ws-info.json');TimeoutSeconds=90;StableSeconds=6;DelaySeconds=3}
    $groups=@(
        [pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Name='Stream Deck';Enabled=$true;OnFailure='Stop';DelayAfterSeconds=0;Apps=@($stream)},
        [pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Name='Wave Link';Enabled=$true;OnFailure='Stop';DelayAfterSeconds=0;Apps=@($wave)},
        [pscustomobject]@{Id=[guid]::NewGuid().ToString('N');Name='Other apps';Enabled=$true;OnFailure='Continue';DelayAfterSeconds=0;Apps=@($existing.Apps)}
    )
    $config=[pscustomobject]@{SchemaVersion=2;UserSid=$existing.UserSid;TaskName='Elgato Ordered Startup';Groups=$groups}
}else{
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $config=[pscustomobject]@{SchemaVersion=2;UserSid=$sid;TaskName=('Startup Manager - '+$sid);Groups=@()}
    Write-StartupJson (Join-Path $PSScriptRoot 'backup.json') ([pscustomobject]@{Registry=@();Tasks=@()})
}
Test-StartupConfiguration $config
Write-StartupJson $configPath $config

