# Startup Manager

A Windows desktop app for organizing sign-in apps into ordered startup groups. The application and startup engine are .NET 8 / C#. No PowerShell runs at startup or while using the app.

## Install

Run **StartupManager-Setup.exe**. The installer includes the .NET runtime, creates a Start menu shortcut, and registers a normal Windows uninstaller. Installation is for the current Windows account. An optional desktop shortcut is available.

Open Startup Manager, create your groups, add apps, and save. Select **Install / repair sequence** to register the sign-in task. Windows requests administrator access under the same account when changing startup entries.

App files normally live in `%LOCALAPPDATA%\Programs\StartupManager`. Configuration, recovery backups, and logs live separately in `%LOCALAPPDATA%\StartupManager\State`. Updates preserve settings. Uninstall restores captured startup entries and removes the runner before deleting the app. If restoration fails or elevation is cancelled, uninstall stops and keeps the app available for recovery. User settings and backups remain after uninstall.

## Groups and readiness

The dark board runs left to right. Group cards expand with the window, and the icon grid adds columns when space allows. Narrow windows scroll the sequence horizontally; each group scrolls its apps vertically. Hover an icon and open its **…** menu to enable/disable, edit, reorder, move, or remove it. App names and launch details appear in tooltips. Disabled apps and groups have dimmed icons. Keyboard users can focus an icon and press Enter or Space to open its menu.

Group menus contain editing, enable/disable, reordering, and deletion. Use the **+** card to create a group and **Import** in a destination group to take over existing startup entries. **Save changes** (Ctrl+S) saves the sequence; **Sequence tools** contains install/repair, readiness checks, logs, and restore. The status bar shows sign-in, conflicts, and unsaved changes, with full diagnostics under **Details**. It offers repair when a migrated task still uses the old runner.

Icons come from locally installed Windows apps, executables, and shortcuts. The board requests large icons when available and falls back to initials for unavailable icons; it does not download icons.

Apps in each enabled group receive their launch requests before the engine polls readiness. The next group starts after every enabled member passes its check. Groups and apps can be disabled, reordered, edited, and removed.

- **Launch accepted + delay:** Windows accepted the launch request; this does not prove that the main app opened.
- **Process is running:** the configured process exists in the current Windows session.
- **Process is responsive:** a matching process reports that it responds.
- **Process exits successfully:** a helper executable exits with code zero.
- **Fresh local server port file:** a process publishes a fresh JSON file with a `port` property and that port is listening. IPv4, IPv6, and HTTP.sys listeners owned by System are supported.

Readiness must remain true for the configured stable time plus extra delay. A timeout fails the group. The default failure policy holds later groups; **Continue** explicitly permits them. A group's optional delay starts after the member checks finish. A required service must already be running; the app does not install or configure services.

For launchers such as Update.exe, enter the final application's process name or use launch accepted with a delay. Process names omit `.exe`. Already-running apps with process checks are reused.

## Existing startup entries

Import discovers eligible Run registry entries, startup-folder files, packaged startup tasks, and logon tasks. Import saves their original settings, disables their independent startup, and puts the selected apps into a group. The sequence must be installed before importing.

Imported entries remain visible as **Disabled** in Task Manager. Enable/disable in an app's menu controls whether the .NET runner launches it. Re-enabling an original entry bypasses group order. The GUI checks for conflicts at opening and every 30 seconds; **Install / repair sequence** disables managed originals again. Install also captures matching manually added apps.

The app excludes selected security entries, Microsoft system tasks, and updater/telemetry/security task names. Review machine-wide entries before importing on a shared PC. Services, drivers, Windows-restored apps, and entries you have not captured retain ordinary behavior.

Import failures restore the entries touched by that operation and restore the previous configuration. If Windows rollback fails, the saved recovery backup is retained and the error is reported. Repair edits the current task definition, preserving upgraded app actions. **Restore original startup** restores captured registry values and task XML and removes the custom sign-in task. Legacy service recovery backups are supported.

There is no telemetry, network dependency, background monitoring service, or requirement to keep the GUI open. Task Scheduler launches `StartupManager.exe --run` at sign-in.

## Existing installations and migration

Schema version 2 is unchanged; see `config.schema.json`. Version 1 migration retains app order, disabled flags, custom arguments, readiness fields, and the original restore backup. The original config is saved as `config.v1.json`.

For a previous portable installation, copy its state into the new location before opening the new app:

~~~powershell
& "$env:LOCALAPPDATA\Programs\StartupManager\StartupManager.exe" --migrate-from 'C:\path\to\old\folder'
~~~

Supply the folder containing `config.json` and `backup.json`, not its `manager` subfolder. Migration refuses another account's state and refuses to overwrite existing settings. An executable still placed in an old `manager` folder also recognizes its adjacent legacy state on first initialization. After migration, use **Install / repair sequence** to replace the old PowerShell scheduled action with the .NET runner. Keep the old folder until repair succeeds.

Do not distribute your state folder: it contains account-specific configuration and recovery backups. Installer payloads include only published binaries, schema, and documentation.

## Build, test, and package

Development requires Windows and the .NET 8 SDK. Creating the installer also requires [Inno Setup 6](https://jrsoftware.org/isinfo.php).

~~~powershell
dotnet build StartupManager.csproj -c Release
dotnet test tests/StartupManager.Tests/StartupManager.Tests.csproj -c Release
dotnet run --project tests/UI.Smoke -c Release
./Build.ps1 -Package
# If ISCC.exe is in a custom location:
./Build.ps1 -Package -InnoCompiler 'C:\tools\Inno Setup 6\ISCC.exe'
~~~

`-Package` runs the tests, publishes self-contained Windows x64 binaries, and creates `release/StartupManager-Setup.exe`. `-UpdateInstalled` updates the default installed app files without changing user state; close the app first. `Build.ps1` is a development helper only and is not shipped. CI runs the same .NET tests and uploads the installer. The executable and installer are currently unsigned.

Tests use fake launchers and a fake clock for ordering, timeouts, stability, and failure policy. Fake Windows operations cover import, re-import, account validation, rollback, install, restore, and migration. Windows integration tests use isolated temporary registry keys, an unstarted temporary scheduled task, and a local TCP listener. They do not change real startup entries or launch user apps.

`tests/Installer.Tests.ps1` runs under PowerShell 7 and creates an isolated installer with a unique product ID, shortcut, install folder, state folder, and task name. It checks installation, upgrades, failed recovery blocking uninstall, successful uninstall, and preservation of settings. CI runs it after packaging.

## Code layout

- `Program.cs`: existing WinForms editor. It calls headless modes of its own executable, elevating only startup changes.
- `Core/Configuration.cs`: JSON validation and version 1 migration.
- `Core/StartupEngine.cs`: launch groups, poll readiness, handle timeouts and group barriers.
- `Core/StartupService.cs`: settings files, backups, import/install/restore transactions, and sequence logging.
- `Core/WindowsSystem.cs`: registry/startup discovery and app readiness using Windows APIs.
- `Core/WindowsTasks.cs`: Task Scheduler COM API and task XML changes.
- `Core/NativeMethods.cs`: small native API bindings for packages, services, and port ownership.
- `Core/BackendCommands.cs`: headless command dispatch, user data paths, and desktop launch.
- `tests/StartupManager.Tests`: xUnit tests.
- `installer/StartupManager.iss`: installer and uninstall recovery hook.

The interfaces cover only Windows operations and time, so tests can replace them. There is no dependency injection framework, generic repository layer, or service host.

Headless modes are `--initialize`, `--state`, `--candidates`, `--install`, `--import`, `--restore`, `--run`, `--uninstall`, and `--migrate-from <folder>`. `--state` and `--candidates` write JSON to standard output; failures return exit code 1 and write `backend-error.log`. `--state-directory <folder>` supports isolated test/development state. Install/import/restore require administrator access with the configured user's SID.
