; LightMarkdownWriter installer (Inno Setup 6).
; Build with installer/build-installer.ps1, which publishes the self-contained app and passes:
;   /DAppVersion=x.y.z   /DSourceDir=<folder with LightMarkdownWriter.exe>
;
; Per-user install (no admin): %LOCALAPPDATA%\Programs\LightMarkdownWriter. The .md registration mirrors
; Services/FileAssociationService.cs exactly, so the app's Settings page sees it as "Registered".

#ifndef AppVersion
  #error AppVersion must be passed on the command line (/DAppVersion=x.y.z)
#endif
#ifndef SourceDir
  #error SourceDir must be passed on the command line (/DSourceDir=...)
#endif

#define AppName "LightMarkdownWriter"
#define AppExe "LightMarkdownWriter.exe"
#define ProgId "LightMarkdownWriter.md"
#define RepoUrl "https://github.com/lucasriechelmann/LightMarkdownWriter"

[Setup]
; Never change AppId: it's how Windows recognizes upgrades and the uninstaller.
AppId={{A1C8516E-3FC9-45F3-80BD-7487ACEC1E01}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Lucas Riechelmann
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809 or later (the README's minimum).
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\src\LightMarkdownWriter\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; Stable name, so the website can link to /releases/latest/download/LightMarkdownWriter-Setup.exe.
OutputBaseFilename={#AppName}-Setup
; Close a running copy during upgrades (it saves its session on shutdown), don't restart it.
CloseApplications=yes
RestartApplications=no
; Refresh Explorer's file associations at the end.
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "mdfiles"; Description: "Open .md and .markdown files with {#AppName} (becomes the default if no other app is)"; GroupDescription: "Files:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ProgId: what "open" means for our file type.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Markdown document"; Tasks: mdfiles
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: mdfiles
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: mdfiles
; "Open with" entries. (Inno's ValueType: none creates only the key, not a value; an empty string works for Windows.)
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Tasks: mdfiles
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Tasks: mdfiles
; Default for double-click, only where no other app is the default (never take over an existing association).
Root: HKA; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Tasks: mdfiles; Check: HasNoDefault('.md')
Root: HKA; Subkey: "Software\Classes\.markdown"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Tasks: mdfiles; Check: HasNoDefault('.markdown')
; Applications entry: friendly name in "Open with".
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: mdfiles
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: mdfiles
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""; Tasks: mdfiles
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""; Tasks: mdfiles
; Capabilities + RegisteredApplications: makes the app appear in Settings > Default apps.
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Tasks: mdfiles
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A light markdown editor with live preview."; Tasks: mdfiles
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "{#ProgId}"; Tasks: mdfiles
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "{#ProgId}"; Tasks: mdfiles
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Tasks: mdfiles

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent
; Windows lets only the user switch defaults. If another app owns .md, offer its Settings page, focused on us.
Filename: "ms-settings:defaultapps?registeredAppUser={#AppName}"; Description: "Make {#AppName} the default app for .md files (opens Windows Settings)"; Flags: shellexec nowait postinstall skipifsilent; Tasks: mdfiles; Check: AnotherAppIsDefault

[Code]
const
  ProgId = '{#ProgId}';
  UserChoiceKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice';

{ True when neither the user nor the machine has a default app for this extension. }
function HasNoDefault(Ext: String): Boolean;
var
  Value: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU, 'Software\Classes\' + Ext, '', Value) and (Value <> '') and (Value <> ProgId) then
    Result := False;
  if RegQueryStringValue(HKLM, 'Software\Classes\' + Ext, '', Value) and (Value <> '') then
    Result := False;
end;

{ True when double-clicking a .md file would open some other app. }
function AnotherAppIsDefault: Boolean;
var
  Chosen, Default: String;
begin
  if RegQueryStringValue(HKCU, UserChoiceKey, 'ProgId', Chosen) and (Chosen <> '') then
    Result := (Chosen <> ProgId) and (Pos(LowerCase('{#AppExe}'), LowerCase(Chosen)) = 0)
  else
    Result := not (RegQueryStringValue(HKCU, 'Software\Classes\.md', '', Default) and (Default = ProgId));
end;

procedure DeleteDefaultIfOurs(Ext: String);
var
  Value: String;
begin
  if RegQueryStringValue(HKCU, 'Software\Classes\' + Ext, '', Value) and (Value = ProgId) then
    RegDeleteValue(HKCU, 'Software\Classes\' + Ext, '');
end;

{ Uninstall removes every association key, whether setup or the app's Settings page created them,
  so nothing is left pointing at a deleted exe. Matches FileAssociationService.Unregister. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\' + ProgId);
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\{#AppExe}');
    RegDeleteValue(HKCU, 'Software\Classes\.md\OpenWithProgids', ProgId);
    RegDeleteValue(HKCU, 'Software\Classes\.markdown\OpenWithProgids', ProgId);
    DeleteDefaultIfOurs('.md');
    DeleteDefaultIfOurs('.markdown');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\{#AppName}\Capabilities');
    RegDeleteKeyIfEmpty(HKCU, 'Software\{#AppName}');
    RegDeleteValue(HKCU, 'Software\RegisteredApplications', '{#AppName}');
    { Tabs, settings and unsaved backups in %LOCALAPPDATA%\LightMarkdownWriter are kept on purpose. }
  end;
end;
