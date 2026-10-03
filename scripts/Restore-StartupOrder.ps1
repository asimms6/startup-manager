$ErrorActionPreference = 'Stop'
$config=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.json') -Raw | ConvertFrom-Json
if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $config.UserSid){throw 'Restore using the account that created this configuration.'}
$backup = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'backup.json') -Raw | ConvertFrom-Json
foreach ($entry in $backup.Registry) {
    if ($entry.Existed) {
        $value = if ($entry.Type -eq 'Binary') { [Convert]::FromBase64String($entry.Value) } else { [int]$entry.Value }
        New-ItemProperty -LiteralPath $entry.Path -Name $entry.Name -PropertyType $entry.Type -Value $value -Force | Out-Null
    } else { Remove-ItemProperty -LiteralPath $entry.Path -Name $entry.Name -ErrorAction SilentlyContinue }
}
foreach ($task in $backup.Tasks) { Register-ScheduledTask -TaskName $task.Name -TaskPath $task.Path -Xml $task.Xml -Force | Out-Null }
if($backup.Service){
if ($backup.Service.FailureActionsExisted) {
    New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\WavelinkSEService' -Name FailureActions -PropertyType Binary -Value ([Convert]::FromBase64String($backup.Service.FailureActions)) -Force | Out-Null
} else {
    & "$env:WINDIR\System32\sc.exe" failure WavelinkSEService reset= 0 actions= '""' | Out-Null
    Remove-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\WavelinkSEService' -Name FailureActions -ErrorAction SilentlyContinue
}
if($backup.Service.PSObject.Properties['Start']){Set-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\WavelinkSEService' -Name Start -Type DWord -Value ([int]$backup.Service.Start)}
}
Unregister-ScheduledTask -TaskName $config.TaskName -Confirm:$false -ErrorAction SilentlyContinue
Write-Output 'Original startup settings restored. The ordered startup task was removed.'
