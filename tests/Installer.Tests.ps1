#requires -Version 7.0
param([Parameter(Mandatory=$true)][string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testId = [guid]::NewGuid().ToString('N')
$fixture = Join-Path $env:TEMP ('StartupManagerInstallerTests-' + $testId)
$app = Join-Path $fixture 'App'
$state = Join-Path $fixture 'State'
$payload = Join-Path $fixture 'Payload'
$productName = 'Startup Manager installer test ' + $testId
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($productName + '.lnk')
$uninstalled = $false
function Run-Checked([string]$File, [string[]]$Arguments, [switch]$ExpectFailure) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Installer test process timed out.' }
    if ($ExpectFailure) {
        if ($process.ExitCode -eq 0) { throw ($File + ' should have failed.') }
    }
    elseif ($process.ExitCode -ne 0) { throw ($File + ' failed with exit code ' + $process.ExitCode) }
    $process.Dispose()
}
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    & dotnet publish (Join-Path $root 'StartupManager.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fixture publish failed.' }
    $productId = '{{' + [guid]::NewGuid().ToString() + '}'
    & $InnoCompiler /Q ('/DPayloadDir=' + $payload) ('/DOutputDir=' + $fixture) ('/DStateDir=' + $state) ('/DProductName=' + $productName) ('/DProductId=' + $productId) (Join-Path $root 'installer\StartupManager.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture installer compile failed.' }
    $setup = Join-Path $fixture 'StartupManager-Setup.exe'
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR=' + $app), ('/LOG=' + (Join-Path $fixture 'install.log')))
    Run-Checked $setup $arguments
    if (-not (Test-Path -LiteralPath $shortcut)) { throw 'Installer did not create its Start menu shortcut.' }
    $exe = Join-Path $app 'StartupManager.exe'
    Run-Checked $exe @('--initialize', '--state-directory', $state)
    $config = Join-Path $state 'config.json'
    # Never let the fixture refer to this account's real sign-in task.
    $fixtureConfig = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    $fixtureConfig.TaskName = $productName
    $fixtureConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config
    $before = [IO.File]::ReadAllText($config)
    Run-Checked $setup $arguments
    if ($before -ne [IO.File]::ReadAllText($config)) { throw 'Upgrade changed user settings.' }
    if (@(Get-ChildItem -LiteralPath $app -Recurse -Filter '*.ps1').Count) { throw 'Installer shipped PowerShell scripts.' }
    # An invalid configuration must stop uninstall before app files are removed.
    $fixtureConfig.SchemaVersion = 99
    $fixtureConfig | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config
    Run-Checked (Join-Path $app 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -ExpectFailure
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Failed recovery removed the app.' }
    [IO.File]::WriteAllText($config, $before)
    Run-Checked (Join-Path $app 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    $uninstalled = $true
    if (Test-Path -LiteralPath $exe) { throw 'Uninstall left the app executable installed.' }
    if (Test-Path -LiteralPath $shortcut) { throw 'Uninstall left its Start menu shortcut.' }
    if ($before -ne [IO.File]::ReadAllText($config)) { throw 'Uninstall deleted or changed user settings.' }
    'PASS: install, upgrade, recovery failure blocks uninstall, successful uninstall, and retained state.'
}
finally {
    $uninstaller = Join-Path $app 'unins000.exe'
    if (-not $uninstalled -and (Test-Path -LiteralPath $uninstaller)) {
        # Clean up a failed test installation before removing its recovery files.
        Run-Checked $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
    # Only this test's GUID-named fixture may be removed recursively.
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\StartupManagerInstallerTests-'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
