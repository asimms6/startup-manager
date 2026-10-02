$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $env:TEMP ('StartupManagerTests-'+[guid]::NewGuid().ToString('N'))
$registry='HKCU:\Software\ElgatoOrderedStartupTests\'+[guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $fixture | Out-Null
New-Item -Path $registry -Force | Out-Null
try{
    Copy-Item -LiteralPath (Join-Path $root 'scripts\Import-StartupApps.ps1') -Destination (Join-Path $fixture 'Import-StartupApps.ps1')
    $common=Get-Content -LiteralPath (Join-Path $root 'scripts\StartupManager.Common.ps1') -Raw
    $common += "`nfunction Get-AvailableStartupApps { `$records=Get-Content -LiteralPath (Join-Path `$PSScriptRoot 'available.json') -Raw | ConvertFrom-Json; foreach (`$record in `$records) { `$record } }`n"
    $common += "`nfunction Get-ScheduledTask { param([string]`$TaskName,`$ErrorAction) if (`$TaskName -eq 'Elgato Ordered Startup') { [pscustomobject]@{State='Ready'} } }`n"
    Set-Content -LiteralPath (Join-Path $fixture 'StartupManager.Common.ps1') -Value $common
    $disabled=New-Object byte[] 12;$disabled[0]=3
    $enabled=New-Object byte[] 12;$enabled[0]=2
    New-ItemProperty -LiteralPath $registry -Name OtherDisabled -PropertyType Binary -Value $disabled | Out-Null
    New-ItemProperty -LiteralPath $registry -Name Example -PropertyType Binary -Value $enabled | Out-Null
    $config=[pscustomobject]@{UserSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;Apps=@([pscustomobject]@{Name='Already off';Kind='Command';Enabled=$false;FileName='C:\example.exe';Arguments=''})}
    $backup=[pscustomobject]@{Registry=@();Tasks=@()}
    $candidate=[pscustomobject]@{Id=($registry+'|Example');Name='Example';SourceKind='Registry';SourcePath=$registry;SourceName='Example';App=[pscustomobject]@{Name='Example';Kind='Command';FileName='C:\example.exe';Arguments='';WorkingDirectory='C:\'}}
    $config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $fixture 'config.json')
    $backup | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $fixture 'backup.json')
    ConvertTo-Json -InputObject @($candidate) -Depth 6 | Set-Content -LiteralPath (Join-Path $fixture 'available.json')
    ConvertTo-Json -InputObject @($candidate.Id) | Set-Content -LiteralPath (Join-Path $fixture 'pending-import.json')
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'Import-StartupApps.ps1')
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath (Join-Path $fixture 'import-result.json') -ErrorAction SilentlyContinue | Write-Output;throw 'First import failed.'}
    $key=Get-Item -LiteralPath $registry
    if($key.GetValue('OtherDisabled')[0] -ne 3 -or $key.GetValue('Example')[0] -ne 3){throw 'Import changed another value or did not disable the selected entry.'}
    $result=Get-Content -LiteralPath (Join-Path $fixture 'config.json') -Raw | ConvertFrom-Json
    if($result.Apps.Count -ne 2 -or $result.Apps[0].Enabled -ne $false){throw 'Import did not preserve the existing disabled app.'}
    Write-Output 'PASS: import preserves sibling registry values and existing disabled apps.'
    Set-ItemProperty -LiteralPath $registry -Name Example -Value $enabled
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'Import-StartupApps.ps1')
    if($LASTEXITCODE -ne 0){throw 'Repeated import failed.'}
    $result=Get-Content -LiteralPath (Join-Path $fixture 'config.json') -Raw | ConvertFrom-Json
    $savedBackup=Get-Content -LiteralPath (Join-Path $fixture 'backup.json') -Raw | ConvertFrom-Json
    if($result.Apps.Count -ne 2 -or $savedBackup.Registry.Count -ne 1 -or [Convert]::FromBase64String($savedBackup.Registry[0].Value)[0] -ne 2){throw 'Duplicate import or original backup corruption.'}
    Write-Output 'PASS: re-import does not duplicate apps or overwrite the original backup.'
    $before=Get-Content -LiteralPath (Join-Path $fixture 'config.json') -Raw
    ConvertTo-Json -InputObject @('missing') | Set-Content -LiteralPath (Join-Path $fixture 'pending-import.json')
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'Import-StartupApps.ps1')
    if($LASTEXITCODE -eq 0){throw 'Stale import should fail.'}
    $after=Get-Content -LiteralPath (Join-Path $fixture 'config.json') -Raw
    if($before.Trim() -ne $after.Trim() -or (Get-Item -LiteralPath $registry).GetValue('OtherDisabled')[0] -ne 3){throw 'Stale import changed configuration.'}
    Write-Output 'PASS: stale startup selections fail without changing the configuration.'
}finally{
    # These exact targets are generated beneath the test-only roots above.
    if($registry -notlike 'HKCU:\Software\ElgatoOrderedStartupTests\*'){throw 'Unsafe registry test path.'}
    Remove-Item -LiteralPath $registry -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    $allowed=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\StartupManagerTests-'
    if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
