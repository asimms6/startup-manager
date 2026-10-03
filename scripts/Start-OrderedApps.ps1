$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'StartupManager.Common.ps1')
. (Join-Path $PSScriptRoot 'StartupEngine.ps1')
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
Test-StartupConfiguration $config
if($config.UserSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'This configuration belongs to a different account.'}
function Write-StartupLog([string]$Message){Add-Content -LiteralPath (Join-Path $PSScriptRoot 'startup.log') -Value ('{0:o} {1}' -f (Get-Date),$Message)}
$mutex=New-Object System.Threading.Mutex($false,('Local\StartupManager-'+$config.UserSid))
$locked=$false
try{
    try{$locked=$mutex.WaitOne(0)}catch [System.Threading.AbandonedMutexException]{$locked=$true}
    if(-not $locked){exit}
    Write-StartupLog 'Sequence started.'
    Invoke-StartupGroups $config
    Write-StartupLog 'Sequence complete.'
}catch{Write-StartupLog ('ERROR: '+$_.Exception.Message);exit 1}
finally{if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()}

