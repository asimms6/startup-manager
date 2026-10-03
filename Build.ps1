param([switch]$UpdateInstalled, [switch]$Package, [string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'StartupManager.csproj'
$publish = Join-Path $PSScriptRoot 'publish'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\StartupManager'
if ($UpdateInstalled -and @(Get-Process -Name StartupManager -ErrorAction SilentlyContinue).Count) {
    throw 'Close Startup Manager before updating it.'
}
& dotnet publish $project -c Release --self-contained false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($UpdateInstalled) {
    if (-not (Test-Path -LiteralPath (Join-Path $installed 'StartupManager.exe'))) { throw 'Install Startup Manager first.' }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained update failed.' }
    Copy-Item -Path (Join-Path $publish '*') -Destination $installed -Force
    Write-Output 'Updated app files. Settings and backups in LocalAppData\StartupManager\State were preserved.'
}
if ($Package) {
    & dotnet test (Join-Path $PSScriptRoot 'tests\StartupManager.Tests\StartupManager.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no installer was created.' }
    # A fresh staging folder prevents stale files from entering the installer.
    $payload = Join-Path $PSScriptRoot ('release\payload-' + [guid]::NewGuid().ToString('N'))
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained build failed.' }
    if (-not $InnoCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InnoCompiler = $command.Source }
        else { $InnoCompiler = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
    }
    if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6, or pass -InnoCompiler with the path to ISCC.exe.' }
    & $InnoCompiler /Q ('/DPayloadDir=' + $payload) ('/DOutputDir=' + (Join-Path $PSScriptRoot 'release')) (Join-Path $PSScriptRoot 'installer\StartupManager.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
    Write-Output ('Installer: ' + (Join-Path $PSScriptRoot 'release\StartupManager-Setup.exe'))
}
