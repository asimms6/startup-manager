# Startup Manager

A local Windows app that launches Stream Deck first, Wave Link 3 second, and selected startup apps afterward. Windows Task Scheduler runs the sequence without the GUI remaining open. No network access, telemetry, or cloud account.

## Share and set up

Send the release ZIP, not your installed folder. The Windows x64 release includes its .NET runtime. It contains no user's configuration, account ID, startup backup, or logs.

1. Extract the entire ZIP to a permanent writable folder. Keep the scripts next to the `manager` folder.
2. Open `manager\StartupManager.exe`. It starts a normal desktop process through a temporary Task Scheduler launch, avoiding inherited packaged-app registry redirection.
3. First run detects the current user's Stream Deck installation and Wave Link 3 package. Both must already be installed; older Wave Link versions are not supported by this release. Nothing is disabled during detection.
4. Click **Install / repair sequence**. Approve Windows' administrator prompt using the same account. Setup captures and disables the original audio-app startup entries and creates the sign-in task.
5. Click **Import startup apps**, select the other apps you want to defer, and import. Each import backs up its original startup entry, disables independent startup, verifies the change, and adds the app to the ordered list. Previously disabled apps are excluded.
6. Sign out and back in when convenient to check the full sign-in sequence.

Keep this folder at its installed location. If you move it, open the app from the new folder and run Install / repair sequence. There should be one installed copy per Windows account. Windows may show an unsigned-app warning; this personal build is not code-signed.

## Maintain

- Check or uncheck an app, reorder it, then **Save changes**. Checked means the ordered runner will launch it.
- Original entries remain listed in Task Manager, with **Disabled** status. Leave them disabled; enabling them there bypasses the order. Task Manager may need to be closed and reopened after changes.
- The app checks managed startup entries when opened and every 30 seconds while open. It shows conflicts when an original entry has been re-enabled. **Install / repair sequence** disables those entries again and verifies them. It does not continuously watch while closed.
- **Import startup apps** also discovers newly enabled entries. New apps are not taken over silently.
- **Add app** adds a manual launch; use Import when an independent startup entry already exists.
- **Edit** changes executable name, path, arguments, and working directory.
- **Check readiness** reports Stream Deck, Wave Link's service/server, the sign-in task, and conflicting independent startup entries.
- **View log** opens `startup.log`.
- **Restore original startup** restores captured registry values, logon task triggers, and Wave Link service recovery settings, then removes the ordered task. It leaves the service running. Administrator access is required.

## What is checked

Stream Deck must remain responsive for five seconds, followed by an initialization grace period. Wave Link's enabler service must be running and its current process must publish a fresh listening local server. If either check fails, the controlled apps are held and an error is logged. These checks do not prove audible speaker output or completion of every Stream Deck plugin.

Windows services, drivers, security components, Windows-restored apps, and entries you have not imported retain their normal startup behavior. Discovery excludes security tray entries and selected updater/system tasks. Machine-wide startup entries can affect other users; review those before importing on a shared PC.

## Source and build

Built with .NET 8 Windows Forms and Windows PowerShell. Windows x64 is the release target; first-run setup requires Stream Deck and Wave Link 3.

```powershell
./Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Import.Tests.ps1
./Build.ps1 -Package
```

`-Package` creates a fresh self-contained Windows x64 ZIP under `release`, copying only build files and scripts. It never copies `.runtime`, configuration, or backups. On the development PC, close the installed app and run `./Build.ps1 -UpdateInstalled` to preserve configuration and backups while replacing the app/scripts.

`Program.cs` contains the GUI. `scripts` contains detection, installation, registry/task control, desktop launch, startup ordering, and restoration. Configuration and undo records live beside the installed scripts. They are per-user data and must not be shipped to another person.

Registry changes set individual values; never recreate existing StartupApproved keys with New-Item -Force, which can erase sibling values. Tests cover preservation, re-import, stale selections, and conflict detection.
