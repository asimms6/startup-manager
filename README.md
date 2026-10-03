# Startup Manager

A small Windows desktop app for organizing login apps into ordered startup groups.

Create groups such as **Audio**, **Work**, and **Everything else**. Apps in each enabled group receive their launch requests before the manager waits for their readiness checks. Once every enabled app in that group passes, the next group starts.

The GUI can stay closed. Windows Task Scheduler runs a short-lived PowerShell runner at sign-in. No network access, telemetry, or background monitoring service.

## Get started

1. Extract the entire Windows x64 release ZIP to a permanent writable folder. Keep the scripts beside the manager folder.
2. Open manager/StartupManager.exe. The included .NET runtime makes the release self-contained.
3. Create a group, give it a name, and add apps. Create more groups and move them into the order you want.
4. Save, then select **Install / repair sequence**. Approve the administrator prompt under the same Windows account.
5. To take over existing Windows startup entries, select a group and use **Import startup apps**. Selected entries are backed up, disabled in Windows, verified, and assigned to that group.
6. Sign out and back in when convenient to validate your full sequence.

Setup does not require any particular app or vendor. New installations start with an empty group list.

## Groups and apps

- Create, rename, enable, disable, reorder, and delete groups.
- Add executables/shortcuts, import packaged apps and eligible logon tasks, and move apps between groups.
- Configure app arguments, working directory, readiness check, timeout, and extra initialization delay.
- Group failure behavior defaults to **Stop remaining groups**. Choosing **Continue** allows later groups even after a launch failure or timeout.
- A group's optional delay starts after its apps have passed their checks.
- Disabling or deleting an app/group removes it from ordered startup. Its original startup entry remains disabled until you restore it.

Launch requests are issued in list order without waiting for each app's readiness. Required service prerequisites can delay an individual launch request. Readiness checks share a polling loop; the next group waits for every enabled member. Already-running apps with process-based checks are reused.

## Readiness checks

| Check | What it means |
|---|---|
| Launch accepted + delay | Windows accepted the launch request. This does not prove that the main app opened. Useful for shortcuts, packaged apps, and launchers. |
| Process is running | The configured process name is present in the current Windows session. |
| Process is responsive | A matching process reports that it is responding. This is not proof that every plugin has initialized. |
| Process exits successfully | A helper executable has finished with exit code zero. |
| Fresh local server port file | A running process publishes a fresh JSON file containing a port property, and that port is listening. Supports HTTP.sys listeners owned by System. |

For a launcher such as Update.exe, configure the final application's process name or choose launch accepted with a delay. Process names are entered without .exe.

Advanced fields allow a required Windows service and a JSON port file. The app waits for an existing required service; it does not install services or change their startup/recovery settings. These generic checks cannot establish audible sound or application-specific readiness beyond the signals you configure.

## Existing Windows startup entries

Task Manager continues to list imported entries, with **Disabled** status. Startup Manager's checked box controls whether its own runner launches the app. Re-enabling the original entry bypasses the group order.

The GUI checks managed entries at opening and every 30 seconds while open. If another app or Windows re-enables an original entry, the manager shows a conflict. **Install / repair sequence** disables it again. The manager does not monitor while closed or silently take over newly installed apps.

Install/repair also detects registered startup commands matching manually added apps and captures/disables them. Import is preferable because it identifies the exact original entry and retains its registered arguments.

Windows services, drivers, security tray entries, Windows-restored apps, and entries you have not imported retain their ordinary behavior. Review machine-wide startup entries before importing on a shared PC. Logon task discovery excludes Microsoft system tasks and selected updater/security tasks.

**Restore original startup** restores captured registry values and task triggers and removes the ordered sign-in task. Restoring an older installation also supports its existing legacy service backup.

## Configuration and migration

Configuration, backups, and logs live beside the installed scripts. Keep the installed folder at its current path, or run Install / repair sequence after moving it.

Schema version 2 stores Groups, each with a unique ID, name, enabled flag, failure policy, delay, and app array. See config.schema.json for the format.

Existing version 1 installations migrate automatically to three editable groups. App order, disabled app flags, custom launch arguments, and the original restore backup are retained. The original configuration is copied to config.v1.json. Generic readiness fields retain the former audio checks without making those applications a requirement for new users.

Do not send an installed folder to another person. Release ZIPs contain no user's configuration, account ID, backup, logs, or debugging symbols.

The GUI first uses a temporary, non-elevated Task Scheduler launch so its desktop process does not inherit a packaged application's private registry view. The temporary task removes itself. The sign-in task is per account for new installations; legacy installations retain their existing task name during migration.

## Build and test

Requires Windows, the .NET 8 SDK, and Windows PowerShell 5.1. The self-contained release target is Windows x64.

~~~powershell
./Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Import.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Groups.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Migration.Tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Controls.Tests.ps1
./Build.ps1 -Package
~~~

-Package creates a fresh self-contained ZIP under release. It copies only the published app, scripts, schema, and README. -UpdateInstalled updates this development checkout's .runtime while preserving local configuration and backups; close the app first.

The GitHub Actions workflow builds, tests, and produces the Windows ZIP as a workflow artifact. Source code has no machine-specific configuration. The desktop executable is unsigned.

Program.cs contains the group editor. scripts/StartupEngine.ps1 implements launches and group barriers. StartupManager.Common.ps1 handles startup discovery, validation, conflicts, and registry/task control. The remaining scripts initialize/migrate, import, install, run, launch the desktop GUI, and restore.

Tests cover group barriers, timeouts, failure policies, disabled groups/apps, preservation of sibling registry values, re-import without duplicates, stale selections, and migration without personal prerequisites. They use mocked launch functions and isolated registry/folder fixtures.


