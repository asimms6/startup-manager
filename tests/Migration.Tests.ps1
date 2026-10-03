$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fixture=Join-Path $env:TEMP ('StartupManagerMigration-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try{
    foreach($name in 'Initialize-State.ps1','StartupManager.Common.ps1'){Copy-Item -LiteralPath (Join-Path $root ('scripts\'+$name)) -Destination $fixture}
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'Initialize-State.ps1')
    if($LASTEXITCODE -ne 0){throw 'Generic first run failed.'}
    $configPath=Join-Path $fixture 'config.json'
    $config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if($config.SchemaVersion -ne 2 -or @($config.Groups).Count -ne 0 -or $config.TaskName -notlike 'Startup Manager -*'){throw 'First run should create empty generic groups.'}
    'PASS: fresh setup has no vendor prerequisite or preselected apps.'
    $legacy=[pscustomobject]@{UserSid=$config.UserSid;StreamDeckPath='C:\Example\StreamDeck.exe';Apps=@([pscustomobject]@{Name='Custom app';Kind='Command';FileName='C:\Example\Custom.exe';Arguments='--custom';Enabled=$false})}
    $legacy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath
    $before=Get-Content -LiteralPath (Join-Path $fixture 'backup.json') -Raw
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'Initialize-State.ps1')
    if($LASTEXITCODE -ne 0){throw 'Legacy migration failed.'}
    $migrated=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if($migrated.Groups.Count -ne 3 -or $migrated.Groups[2].Apps[0].Enabled -ne $false -or $migrated.Groups[2].Apps[0].Arguments -ne '--custom'){throw 'Migration lost app order/settings.'}
    if($migrated.Groups[0].Apps[0].WaitMode -ne 'Responsive' -or $migrated.Groups[1].Apps[0].WaitMode -ne 'PortFile'){throw 'Migration lost former readiness checks.'}
    if($before -ne (Get-Content -LiteralPath (Join-Path $fixture 'backup.json') -Raw)){throw 'Migration changed the restore backup.'}
    if(-not(Test-Path -LiteralPath (Join-Path $fixture 'config.v1.json'))){throw 'Original configuration was not retained.'}
    'PASS: migration keeps custom apps, disabled flags, readiness, and original backups.'
    $snapshot=Get-Content -LiteralPath $configPath -Raw
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture 'Initialize-State.ps1')
    if($LASTEXITCODE -ne 0 -or $snapshot -ne (Get-Content -LiteralPath $configPath -Raw)){throw 'Migration was not idempotent.'}
    'PASS: reopening an already migrated configuration leaves it unchanged.'
}finally{
    $resolved=[IO.Path]::GetFullPath($fixture)
    $allowed=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\StartupManagerMigration-'
    if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
