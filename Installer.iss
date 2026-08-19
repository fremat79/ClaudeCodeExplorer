; =====================================================================
;  Claude Code Explorer - Inno Setup script
;  Produces a Windows installer (Setup .exe) for the published app.
;
;  Build the app first, then compile this script:
;     dotnet publish -c Release -p:PublishProfile=FolderProfile
;     "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" Installer.iss
;
;  Output: dist\ClaudeCodeExplorer-Setup-<version>.exe
;  Note: Inno Setup emits a setup .exe (not a .msi). Use WiX if you need .msi.
; =====================================================================

#define MyAppName        "Claude Code Explorer"
#define MyAppPublisher   "Matteo Freddi"
#define MyAppURL         "https://github.com/fremat79/ClaudeCodeExplorer"
#define MyAppExeName      "ClaudeCodeExplorer.exe"

; Folder that contains the published files (relative to this .iss in the repo root).
#define PublishDir       "bin\Release\net10.0-windows\publish\win-x64"

; Read the version straight from the published executable (falls back to 1.0.0.0).
#ifexist PublishDir + "\" + MyAppExeName
  #define MyAppVersion   GetFileVersion(PublishDir + "\" + MyAppExeName)
#endif
#ifndef MyAppVersion
  #define MyAppVersion   "1.0.0.0"
#endif

[Setup]
AppId={{9F3B7A21-6C4E-4D8A-B2F1-3E7C9A5D1B08}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
; Per-user install (no admin prompt), compatible with Inno Setup 5.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=none
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=dist
OutputBaseFilename=ClaudeCodeExplorer-Setup-{#MyAppVersion}
SetupIconFile=Assets\claude.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Ship everything produced by the publish step (exe + native SQLite + any future files).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";           Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}";     Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch after install. The app starts hidden in the system tray.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
{ ---- Close a running instance so files aren't locked ---- }
procedure KillRunning();
var
  RC: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /f /im ' + '{#MyAppExeName}' + ' >nul 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, RC);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    KillRunning();
end;

{ ---- Uninstall cleanup: stop the app, remove its autostart entry, optionally its data ---- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillRunning();
    { The app registers autostart under HKCU\...\Run via its own Settings; remove the leftover. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ClaudeCodeExplorer');

    DataDir := ExpandConstant('{localappdata}\ClaudeCodeExplorer');
    if DirExists(DataDir) then
      if MsgBox('Also delete cached data (search index, cache, settings) in:' + #13#10 +
                DataDir + ' ?', mbConfirmation, MB_YESNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
