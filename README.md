# Startup Manager

A small Windows desktop app for maintaining the local Stream Deck → Wave Link → other apps startup sequence. Built with .NET 8 Windows Forms and the Windows PowerShell/Task Scheduler tools already installed on this PC. No third-party packages, background agent, telemetry, or cloud account.

## Use

Open **Startup Manager** from the Start menu. The app does not need to remain open.

- Check or uncheck an app, change its position, then **Save changes**.
- **Add app** adds an executable or shortcut to the list. If it already starts independently with Windows, use **Import startup apps** instead, so its original autorun entry is disabled.
- **Import startup apps** finds enabled desktop autoruns, packaged app startup tasks, and selected third-party logon tasks. Choose the entries to move into the sequence. Import requests administrator access and preserves an undo record. Previously disabled apps are excluded.
- **Edit** changes an executable's name, path, arguments, and working directory. Packaged app and scheduled task launches retain their registered settings.
- **Check readiness** reports the Stream Deck process, Wave Link service/server, and startup task.
- **Install / repair sequence** recreates the task and startup controls from the saved configuration if needed. It asks for administrator access.
- **View log** opens the sequence log.
- **Restore original startup** restores the captured startup flags/task triggers and original service recovery settings, and removes the ordered startup task. It leaves Wave Link's service running.

Stream Deck and Wave Link are fixed first/second stages. Their initialization checks are not proof of audible speaker output or completion of every plugin. Windows services, drivers, security components, and apps restored by Windows retain their normal startup behavior. Apps that add or re-enable an independent startup entry need to be imported; the manager does not silently take over newly installed apps.

## Installation on this PC

Executable: `.runtime\manager\StartupManager.exe` in this repo.

Task: `Elgato Ordered Startup`, at Simms sign-in, interactive and non-elevated.

Runtime scripts, configuration, undo backup, and log: `.runtime` in this repo. This folder is ignored by Git. Keeping runtime data here avoids packaged-app filesystem redirection and gives the app and scheduled task the same files.

The source repository is separate from runtime configuration. Installed app names, commands, task XML, and account IDs are not committed to this repository.

Keep this repository at its current path: the installed app and sign-in task use its `.runtime` folder.

Wave Link's standalone enabler service was stopped after a crash. Its startup remains automatic, and Windows now retries service failures after 5/10/30 seconds. These recovery settings are included in the undo backup.

## Build and test

```powershell
dotnet publish -c Release --self-contained false -o publish
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Import.Tests.ps1
publish\StartupManager.exe --self-test
```

The app uses the existing .NET 8 Windows Desktop runtime. The self-test validates the installed runtime configuration. Import integration tests use an isolated temporary folder/registry key and do not alter the real startup configuration or service.

To update after editing the source, close Startup Manager and run `./Build.ps1 -UpdateInstalled`. This replaces the app/scripts while preserving configuration and recovery backups.

`Program.cs` contains the GUI. `scripts` contains the startup runner, state exporter, import helper, common registry/package/task discovery, and recovery scripts. The installer scripts were created for this PC's captured configuration; they are not a generic installer for another PC. `Prepare` tooling and user-specific captured configuration are intentionally excluded.

Startup registry writes must set individual values. Do not use `New-Item -Force` on existing registry keys: that can erase sibling values. The import regression test verifies sibling disabled entries survive.
