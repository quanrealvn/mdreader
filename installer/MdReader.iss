; MdReader per-user installer (docs/ARCHITECTURE.md section 12). Built by installer\build.ps1:
;   ISCC.exe /DMyAppVersion=1.0.0 /DPublishDir=<repo>\artifacts\publish\win-x64 installer\MdReader.iss
; Output: installer\output\MdReader-Setup.exe (git-ignored).
;
; Rules this script follows:
;   * Per-user only (PrivilegesRequired=lowest): every registry value lives under HKCU and the app goes to
;     %LOCALAPPDATA%\Programs\MdReader ({userpf}).
;   * Never makes MdReader the default handler: it registers as a *candidate* (OpenWithProgids, Capabilities,
;     RegisteredApplications) and only offers to open Settings > Default apps.
;   * Uninstall removes exactly what Setup added. Shared keys (.md, .markdown, ..., Applications, App Paths,
;     RegisteredApplications) only lose our value/subkey; they're deleted only when Setup created them and they're empty.
;   * An update is an update, not a second product: same AppId, same install folder, same shortcut, same uninstall
;     entry, same registration. The previous payload in {app} is cleared out first ([InstallDelete]) so no assembly
;     from an older release is left behind, and user data is never part of that.

#if Ver < EncodeVer(6, 6, 0)
  #error Inno Setup 6.6 or later is required (WizardStyle=modern dynamic)
#endif

#define RepoRoot AddBackslash(SourcePath) + ".."
#ifndef PublishDir
  #define PublishDir RepoRoot + "\artifacts\publish\win-x64"
#endif
#define AppExeSource PublishDir + "\MdReader.exe"
#if !FileExists(AppExeSource)
  #error MdReader.exe wasn't found in the publish folder. Run installer\build.ps1 (or pass /DPublishDir=<folder>).
#endif

; Product metadata comes from the published exe (Directory.Build.props), so there is one source of truth.
#ifndef MyAppVersion
  #define MyAppVersion GetVersionNumbersString(AppExeSource)
#endif
#define MyAppPublisher GetFileCompany(AppExeSource)
#if MyAppPublisher == ""
  #define MyAppPublisher "MdReader"
#endif
#define MyAppCopyright GetFileCopyright(AppExeSource)

#define MyAppName "MdReader"
#define MyAppExeName "MdReader.exe"
#define MyProgId "MdReader.Markdown"
#define MyAppDescription "Markdown reader with GitHub-style rendering, Mermaid diagrams and math."

[Setup]
; AppId never changes: upgrades, the uninstall entry (HKCU\...\Uninstall\{5FBD80EA-...}_is1) and verify-install.ps1 rely on it.
AppId={{5FBD80EA-E813-46B4-8272-473A2D40CEB7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright={#MyAppCopyright}
AppComments={#MyAppDescription}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright={#MyAppCopyright}
VersionInfoDescription={#MyAppName} Setup
; Per-user install. With PrivilegesRequired=lowest, {userpf} = %LOCALAPPDATA%\Programs (the per-user Program Files),
; so the default folder is %LOCALAPPDATA%\Programs\MdReader. No overrides are allowed, so it's always per-user.
PrivilegesRequired=lowest
DefaultDirName={userpf}\{#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; .NET 10 needs Windows 10 1607 or later.
MinVersion=10.0.14393
ChangesAssociations=yes
; Restart Manager closes a running MdReader before files are replaced (no AppMutex: our mutex name contains the SID).
CloseApplications=yes
SetupMutex=MdReaderSetup-5FBD80EA-E813-46B4-8272-473A2D40CEB7
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#RepoRoot}\src\MdReader.Ui\Assets\MdReader.ico
WizardStyle=modern dynamic
; Generated from the app icon by assets\New-WizardImages.ps1; Setup picks the size that fits the DPI.
WizardImageFile=assets\wizard-light-*.png
WizardImageFileDynamicDark=assets\wizard-dark-*.png
WizardSmallImageFile=assets\wizard-small-*.png
WizardSmallImageFileDynamicDark=assets\wizard-small-*.png
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir=output
OutputBaseFilename=MdReader-Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; An update lays the whole payload down again, but it can't overwrite what the new payload doesn't contain. Going from
; 1.3.x (WPF) to 1.4 (Avalonia) swaps one UI stack for another: 56 files of the old payload, 18 of them WPF assemblies,
; have no counterpart in the new one. They would sit in the install folder forever, be loaded by nothing and still be
; listed in the uninstall log; the same goes for a web\ file a later release drops. So the old payload is removed
; first and then written fresh.
; Only files this installer put there are named. unins000.* (the uninstaller and the uninstall log an upgrade appends
; to) is left alone, and so is everything under %APPDATA%\MdReader and %LOCALAPPDATA%\MdReader. HasPreviousPayload
; keeps this to folders that really hold a previous MdReader, so a first install - or a folder the user picked that
; holds something else - is never touched.
Type: filesandordirs; Name: "{app}\web"; Check: HasPreviousPayload
Type: filesandordirs; Name: "{app}\runtimes"; Check: HasPreviousPayload
Type: files; Name: "{app}\*.dll"; Check: HasPreviousPayload
Type: files; Name: "{app}\*.json"; Check: HasPreviousPayload
Type: files; Name: "{app}\*.pdb"; Check: HasPreviousPayload
Type: files; Name: "{app}\*.xml"; Check: HasPreviousPayload
Type: files; Name: "{app}\{#MyAppExeName}"; Check: HasPreviousPayload
Type: files; Name: "{app}\createdump.exe"; Check: HasPreviousPayload
Type: files; Name: "{app}\THIRD-PARTY-NOTICES.md"; Check: HasPreviousPayload

[Files]
; The whole self-contained publish folder: exe, runtime, WebView2Loader.dll (root and runtimes\win-x64\native), web\.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Read Markdown documents"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Read Markdown documents"; Tasks: desktopicon

[Registry]
; ---- Shared parent keys. Deleted on uninstall only if Setup created them (IsNewKey) and nothing else is left in them.
;      They must come before any entry that writes below them: IsNewKey looks at the key when its entry is processed.
Root: HKCU; Subkey: "Software\Classes\.md"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.md')
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.md\OpenWithProgids')
Root: HKCU; Subkey: "Software\Classes\.markdown"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.markdown')
Root: HKCU; Subkey: "Software\Classes\.markdown\OpenWithProgids"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.markdown\OpenWithProgids')
Root: HKCU; Subkey: "Software\Classes\.mdown"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.mdown')
Root: HKCU; Subkey: "Software\Classes\.mdown\OpenWithProgids"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.mdown\OpenWithProgids')
Root: HKCU; Subkey: "Software\Classes\.mkd"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.mkd')
Root: HKCU; Subkey: "Software\Classes\.mkd\OpenWithProgids"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.mkd\OpenWithProgids')
Root: HKCU; Subkey: "Software\Classes\.mkdn"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.mkdn')
Root: HKCU; Subkey: "Software\Classes\.mkdn\OpenWithProgids"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\.mkdn\OpenWithProgids')
Root: HKCU; Subkey: "Software\Classes\Applications"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Classes\Applications')
Root: HKCU; Subkey: "Software\RegisteredApplications"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\RegisteredApplications')
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\Microsoft\Windows\CurrentVersion\App Paths')
Root: HKCU; Subkey: "Software\{#MyAppName}"; Flags: uninsdeletekeyifempty; Check: IsNewKey('Software\{#MyAppName}')

; ---- ProgID (ours: the whole key goes on uninstall).
Root: HKCU; Subkey: "Software\Classes\{#MyProgId}"; ValueType: string; ValueName: ""; ValueData: "Markdown Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#MyProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Markdown Document"
Root: HKCU; Subkey: "Software\Classes\{#MyProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKCU; Subkey: "Software\Classes\{#MyProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; ---- "Open with" candidates. Only our value is removed on uninstall; the extension keys are shared with other apps.
;      The user's default (UserChoice) is never touched: Windows asks the user.
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mdown\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mkd\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\.mkdn\OpenWithProgids"; ValueType: string; ValueName: "{#MyProgId}"; ValueData: ""; Flags: uninsdeletevalue

; ---- Applications\MdReader.exe ("Open with" list entry and supported types; ours).
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mdown"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mkd"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".mkdn"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

; ---- Default Programs registration (Settings > Default apps lists MdReader; ours).
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{#MyAppDescription}"
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "{#MyProgId}"
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "{#MyProgId}"
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdown"; ValueData: "{#MyProgId}"
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkd"; ValueData: "{#MyProgId}"
Root: HKCU; Subkey: "Software\{#MyAppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkdn"; ValueData: "{#MyProgId}"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: "Software\{#MyAppName}\Capabilities"; Flags: uninsdeletevalue

; ---- App Paths: lets "MdReader.exe" start from Win+R / ShellExecute (and the VS Code extension find it). Ours.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#MyAppExeName}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent unchecked
; Windows doesn't let installers set the default app; this only opens Settings on MdReader's Default apps page.
Filename: "{code:GetDefaultAppsUri}"; Description: "Choose {#MyAppName} as the default app for Markdown files"; Flags: shellexec nowait postinstall skipifsilent

[Code]
const
  WebView2ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2DownloadUrl = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  RegisteredAppName = 'MdReader';
  AppExeName = 'MdReader.exe';

var
  WebView2Version: String;
  WebView2Page: TOutputMsgWizardPage;
  SeenKeys: TStringList;
  NewKeys: TStringList;
  PayloadChecked: Boolean;
  PayloadFound: Boolean;

{ ---------- Files: is there a previous payload in the app folder to clear out? ---------- }

{ Answered once, before [InstallDelete] removes anything: the later entries ask after MdReader.exe is already gone. }
function HasPreviousPayload(): Boolean;
var
  AppDir: String;
begin
  if not PayloadChecked then
  begin
    PayloadChecked := True;
    AppDir := ExpandConstant('{app}');
    PayloadFound := FileExists(AddBackslash(AppDir) + AppExeName) or
                    FileExists(AddBackslash(AppDir) + 'unins000.dat');
    if PayloadFound then
      Log('A previous MdReader payload is in ' + AppDir + ': it is removed before the new files are written.')
    else
      Log('No previous MdReader payload in ' + AppDir + ': nothing to remove.');
  end;
  Result := PayloadFound;
end;

{ ---------- Registry: remember which shared parent keys Setup creates ---------- }

{ True when HKCU\SubKey doesn't exist yet. The first answer per key is kept, so repeated Check calls can't change it
  after an earlier entry created the key. }
function IsNewKey(const SubKey: String): Boolean;
begin
  if SeenKeys = nil then
  begin
    SeenKeys := TStringList.Create;
    NewKeys := TStringList.Create;
  end;
  if SeenKeys.IndexOf(SubKey) < 0 then
  begin
    SeenKeys.Add(SubKey);
    if not RegKeyExists(HKCU, SubKey) then
    begin
      NewKeys.Add(SubKey);
      Log('Registry key will be created (and removed on uninstall if empty): HKCU\' + SubKey);
    end;
  end;
  Result := NewKeys.IndexOf(SubKey) >= 0;
end;

{ ---------- Microsoft Edge WebView2 Runtime ---------- }

function ReadWebView2Version(const RootKey: Integer; const SubKey: String; var Version: String): Boolean;
begin
  Version := '';
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version);
  if Result then
    Result := (Version <> '') and (Version <> '0.0.0.0');
end;

{ Per Microsoft's detection guidance: per-machine (32-bit view, then native view) or per-user install, 'pv' set and not 0.0.0.0. }
function DetectWebView2Runtime(): String;
var
  Version: String;
begin
  Result := '';
  if ReadWebView2Version(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2ClientKey, Version) then
    Result := Version
  else if ReadWebView2Version(HKLM, 'SOFTWARE\' + WebView2ClientKey, Version) then
    Result := Version
  else if ReadWebView2Version(HKCU, 'Software\' + WebView2ClientKey, Version) then
    Result := Version;
end;

procedure OpenWebView2DownloadPage(Sender: TObject);
var
  ErrorCode: Integer;
begin
  if not ShellExec('open', WebView2DownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('The download page couldn''t be opened (' + SysErrorMessage(ErrorCode) + ').' + #13#10#13#10 +
      'Open this address in a browser:' + #13#10 + WebView2DownloadUrl, mbError, MB_OK);
end;

function InitializeSetup(): Boolean;
begin
  WebView2Version := DetectWebView2Runtime();
  if WebView2Version <> '' then
    Log('Microsoft Edge WebView2 Runtime found: version ' + WebView2Version)
  else
    Log('Microsoft Edge WebView2 Runtime not found: Setup offers the download page and continues.');
  Result := True;
end;

procedure InitializeWizard();
var
  Button: TNewButton;
begin
  { Shown first (there is no Welcome page), and only when the runtime is missing (ShouldSkipPage). }
  WebView2Page := CreateOutputMsgPage(wpWelcome,
    'Microsoft Edge WebView2 Runtime',
    'MdReader needs a Windows component that isn''t installed on this computer.',
    'MdReader shows documents with the Microsoft Edge WebView2 Runtime, a free component from Microsoft. ' +
    'It''s built into current versions of Windows 11, but it isn''t installed here.' + #13#10#13#10 +
    'Setup will install MdReader anyway. Before you start MdReader, download and run the Evergreen Bootstrapper ' +
    'from Microsoft''s download page.' + #13#10#13#10 +
    'Click Open download page, then click Next to continue.');
  Button := TNewButton.Create(WebView2Page);
  Button.Parent := WebView2Page.Surface;
  Button.Caption := '&Open download page';
  Button.Width := WizardForm.CalculateButtonWidth(['&Open download page']);
  Button.Height := WizardForm.NextButton.Height;
  Button.Left := 0;
  Button.Top := WebView2Page.MsgLabel.Top + WebView2Page.MsgLabel.Height + ScaleY(16);
  Button.OnClick := @OpenWebView2DownloadPage;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (WebView2Page <> nil) and (PageID = WebView2Page.ID) and (WebView2Version <> '');
end;

{ ---------- Finished page: Settings > Default apps ---------- }

{ ms-settings:defaultapps?registeredAppUser=<value name under HKCU\Software\RegisteredApplications> opens MdReader's own
  page. Supported from Windows 11 21H2 build 22000.1817 / 22H2 build 22621.1555 (2023-04 cumulative update) and 23H2+;
  older systems get the general Default apps page. }
function GetDefaultAppsUri(Param: String): String;
var
  Version: TWindowsVersion;
  Revision: Cardinal;
begin
  Result := 'ms-settings:defaultapps';
  GetWindowsVersionEx(Version);
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'UBR', Revision) then
    Revision := 0;
  if (Version.Build > 22621) or ((Version.Build = 22621) and (Revision >= 1555)) or
     ((Version.Build = 22000) and (Revision >= 1817)) then
    Result := Result + '?registeredAppUser=' + RegisteredAppName;
end;

{ ---------- Uninstall ---------- }

{ Counts MdReader.exe processes started from AppDir (other copies, e.g. a dev build, are left alone); optionally ends them. }
function CountAppProcesses(const AppDir: String; Terminate: Boolean): Integer;
var
  Locator, Service, Processes, Process, ExePathValue: Variant;
  Prefix, ExePath: String;
  I, Count, ProcessId: Integer;
begin
  Result := 0;
  Prefix := Lowercase(AddBackslash(AppDir));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''' + AppExeName + '''');
    Count := Processes.Count;
    for I := 0 to Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      ExePathValue := Process.ExecutablePath;
      if not VarIsNull(ExePathValue) then
      begin
        ExePath := ExePathValue;
        if Pos(Prefix, Lowercase(ExePath)) = 1 then
        begin
          Result := Result + 1;
          if Terminate then
          begin
            ProcessId := Process.ProcessId;
            Log('Ending MdReader process ' + IntToStr(ProcessId) + ' (' + ExePath + ')');
            Process.Terminate(0);
          end;
        end;
      end;
    end;
  except
    Log('Couldn''t check for running MdReader processes: ' + GetExceptionMessage);
  end;
end;

function InitializeUninstall(): Boolean;
var
  AppDir: String;
  Waited: Integer;
begin
  Result := True;
  AppDir := ExpandConstant('{app}');
  while CountAppProcesses(AppDir, False) > 0 do
  begin
    if UninstallSilent then
    begin
      { Silent uninstall can't ask: end the processes so their files can be removed. }
      CountAppProcesses(AppDir, True);
      Waited := 0;
      while (CountAppProcesses(AppDir, False) > 0) and (Waited < 10000) do
      begin
        Sleep(250);
        Waited := Waited + 250;
      end;
      Exit;
    end;
    if MsgBox('MdReader is running. Close all MdReader windows, then click Retry.' + #13#10#13#10 +
              'Click Cancel to stop uninstalling.', mbError, MB_RETRYCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

procedure DeleteUserDataFolder(const Folder: String);
begin
  if not DirExists(Folder) then
    Exit;
  if DelTree(Folder, True, True, True) then
    Log('Deleted ' + Folder)
  else
    MsgBox('Some files in ' + Folder + ' couldn''t be deleted. You can delete the folder yourself.', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsFolder, DataFolder: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  SettingsFolder := ExpandConstant('{userappdata}\MdReader');   { settings.json }
  DataFolder := ExpandConstant('{localappdata}\MdReader');       { logs, WebView2 browser data }
  if (not DirExists(SettingsFolder)) and (not DirExists(DataFolder)) then
    Exit;
  if UninstallSilent then
  begin
    Log('Silent uninstall: keeping ' + SettingsFolder + ' and ' + DataFolder);
    Exit;
  end;
  if MsgBox('Do you also want to delete your MdReader settings, logs and browser data?' + #13#10#13#10 +
            SettingsFolder + #13#10 + DataFolder + #13#10#13#10 +
            'Click No to keep them (for example, if you plan to reinstall MdReader).',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DeleteUserDataFolder(SettingsFolder);
    DeleteUserDataFolder(DataFolder);
  end
  else
    Log('Keeping ' + SettingsFolder + ' and ' + DataFolder);
end;
