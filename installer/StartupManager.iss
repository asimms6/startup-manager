#ifndef PayloadDir
  #define PayloadDir "..\release\payload"
#endif
#ifndef StateDir
  #define StateDir "{localappdata}\StartupManager\State"
#endif
#ifndef ProductName
  #define ProductName "Startup Manager"
#endif
#ifndef ProductId
  #define ProductId "{{885F11D4-69A5-46CD-BF42-F8842E710DB6}"
#endif
#ifndef OutputDir
  #define OutputDir "..\release"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion=1.0.0
AppPublisher=Startup Manager
DefaultDirName={localappdata}\Programs\StartupManager
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename=StartupManager-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\StartupManager.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\config.schema.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\StartupManager.exe"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\StartupManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StartupManager.exe"; Description: "Open Startup Manager"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Restored: Boolean;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  { Restore startup entries before removing their runner. Keep user settings. }
  Restored := Exec(ExpandConstant('{app}\StartupManager.exe'),
    '--uninstall --state-directory "' + ExpandConstant('{#StateDir}') + '"',
    ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Restored then
    Restored := ResultCode = 0;
  if not Restored then
  begin
    SuppressibleMsgBox('Startup settings could not be restored. Uninstall was cancelled. Open Startup Manager and use Restore original startup, then try again.', mbError, MB_OK, IDOK);
    Abort;
  end;
end;
