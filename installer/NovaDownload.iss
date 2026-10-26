; NovaDownload — Windows installer (Inno Setup 6).
;
; Build:
;   powershell -File installer\build-release.ps1
; or directly (after staging the payload):
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\NovaDownload.iss
;
; Behaviour:
;   - installs for all users (Program Files, admin) or for the current user only
;     (the wizard offers the choice; /CURRENTUSER on the command line skips the question);
;   - Start Menu shortcuts, an optional desktop shortcut and a Windows Settings uninstall entry;
;   - ships a self-contained payload: no .NET runtime prerequisite;
;   - registers the native messaging host for Chrome, Edge and Firefox (per user, no admin);
;   - registers the novadownload:// URL protocol as a fallback channel;
;   - upgrades in place and never touches %APPDATA%\NovaDownload or %LOCALAPPDATA%\NovaDownload;
;   - the browser extension packages land in %LOCALAPPDATA%\NovaDownload\extensions, which the
;     uninstaller deliberately leaves alone (an unpacked extension must keep working).

#define AppName "NovaDownload"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "NovaDownload"
#define AppExeName "NovaDownload.App.exe"
#define HostExeName "NovaDownload.NativeHost.exe"
#define StagingDir "..\build\app"
#define ExtensionsDir "..\build\extensions"

; Keep this in sync with BrowserIntegrationInfo.InstallerAppId (asserted by the unit tests).
#define AppId "{{7C2E6B31-1E6D-4C37-9F52-6C2D0E6A4B21}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=Download manager with browser integration
DefaultDirName={autopf}\NovaDownload
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\release\windows-installer
OutputBaseFilename=NovaDownload-{#AppVersion}-win-x64-setup
SetupIconFile=..\assets\novadownload.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
AllowUNCPath=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription=NovaDownload {#AppVersion} setup
VersionInfoCompany={#AppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Application payload (self-contained: runtime, engine, native messaging host).
Source: "{#StagingDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Browser extension packages: per-user, stable, deliberately preserved by the uninstaller.
Source: "{#ExtensionsDir}\*"; DestDir: "{localappdata}\NovaDownload\extensions"; \
    Flags: ignoreversion recursesubdirs createallsubdirs uninsneveruninstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Download manager"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; novadownload:// fallback channel, registered per user under HKCU\Software\Classes (same key the app writes).
Root: HKCU; Subkey: "Software\Classes\novadownload"; ValueType: string; ValueName: "URL:NovaDownload Protocol"; \
    ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\novadownload"; ValueType: string; ValueName: "URL Protocol"; \
    ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\novadownload\DefaultIcon"; ValueType: string; ValueName: ""; \
    ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\novadownload\shell\open\command"; ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" ""%1"""

; magnet: - a link clicked anywhere in Windows opens the swarm engine directly. Windows keeps one
; handler per scheme, so this takes magnet: from any other BitTorrent client; the setting in the
; application gives it back.
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; \
    ValueData: "URL:NovaDownload magnet handler"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; \
    ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; \
    ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey

; .torrent - a double-clicked torrent opens the picker. A ProgID keeps the association declarative
; and leaves the per-user choice Windows records in UserChoice untouched.
Root: HKCU; Subkey: "Software\Classes\NovaDownload.Torrent"; ValueType: string; ValueName: ""; \
    ValueData: "NovaDownload torrent"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\NovaDownload.Torrent\DefaultIcon"; ValueType: string; ValueName: ""; \
    ValueData: "{app}\{#AppExeName},0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\NovaDownload.Torrent\shell\open\command"; ValueType: string; ValueName: ""; \
    ValueData: """{app}\{#AppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; \
    ValueData: "NovaDownload.Torrent"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: "Content Type"; \
    ValueData: "application/x-bittorrent"; Flags: uninsdeletevalue

[Run]
; Native messaging manifests + per-user registry keys for Chrome, Edge and Firefox.
Filename: "{app}\{#HostExeName}"; Parameters: "--register"; \
    Flags: runhidden waituntilterminated runascurrentuser; StatusMsg: "Registering the browser integration..."
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent
Filename: "explorer.exe"; Parameters: """{localappdata}\NovaDownload\extensions"""; \
    Description: "Open the browser extension folder (load them in Chrome, Edge or Firefox)"; \
    Flags: postinstall skipifsilent nowait unchecked

[UninstallRun]
Filename: "{app}\{#HostExeName}"; Parameters: "--unregister"; RunOnceId: "UnregisterNativeHost"; \
    Flags: runhidden waituntilterminated runascurrentuser

[Code]
{ The uninstaller keeps the user's settings, history and browser extensions on purpose. Say so. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir: String;
  ExtensionsDir: String;
  Message: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    SettingsDir := ExpandConstant('{userappdata}\NovaDownload');
    ExtensionsDir := ExpandConstant('{localappdata}\NovaDownload\extensions');
    Message := '';

    if DirExists(SettingsDir) then
      Message := Message + 'Settings and download history were kept in:' + #13#10 + SettingsDir + #13#10#13#10;
    if DirExists(ExtensionsDir) then
      Message := Message + 'Browser extension packages were kept in:' + #13#10 + ExtensionsDir + #13#10#13#10;

    if Message <> '' then
      MsgBox(Message + 'Delete those folders to remove them as well.', mbInformation, MB_OK);
  end;
end;
