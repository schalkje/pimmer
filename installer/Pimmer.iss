; Inno Setup script for Pimmer. Build with scripts\build-installer.ps1.
; Per-user install (no admin rights). User data in %LOCALAPPDATA%\Pimmer is kept on upgrade.
;
; Silent / department rollout:
;   Pimmer-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
;       [/TenantId=contoso.com] [/GraphClientId=<guid>] [/ArmClientId=<guid>]
;   Silent uninstall keeps user data; add /REMOVEDATA to delete it.
; A defaults.json placed next to the installer is also copied to the install folder.
; Defaults only seed settings on a user's first run; they never overwrite existing settings.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{8D1C2E5B-6F3A-4B7E-9A52-3C0D5E7F1A64}
AppName=Pimmer
AppVersion={#AppVersion}
AppVerName=Pimmer {#AppVersion}
AppPublisher=schalken.net
AppCopyright=Copyright (C) 2026 schalken.net
DefaultDirName={localappdata}\Programs\Pimmer
DefaultGroupName=Pimmer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Pimmer-Setup-{#AppVersion}
SetupIconFile=..\src\Pimmer.App\pimmer.ico
UninstallDisplayIcon={app}\Pimmer.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoCompany=schalken.net
VersionInfoDescription=Pimmer installer

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Pimmer"; Filename: "{app}\Pimmer.exe"; Comment: "Pimmer - PIM manager"
Name: "{autodesktop}\Pimmer"; Filename: "{app}\Pimmer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Pimmer.exe"; Description: "Launch Pimmer"; Flags: nowait postinstall skipifsilent

[Code]
function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure AddSetting(var Json: String; const Name, ParamName: String);
var
  Value: String;
begin
  Value := ExpandConstant('{param:' + ParamName + '|}');
  if Value = '' then Exit;
  if Json <> '' then Json := Json + ',';
  Json := Json + '"' + Name + '":"' + JsonEscape(Value) + '"';
end;

procedure WriteDefaults;
var
  Json, Target, Shipped: String;
begin
  Target := ExpandConstant('{app}\defaults.json');
  AddSetting(Json, 'TenantId', 'TenantId');
  AddSetting(Json, 'GraphClientId', 'GraphClientId');
  AddSetting(Json, 'ArmClientId', 'ArmClientId');
  Shipped := ExpandConstant('{src}\defaults.json');
  if Json <> '' then
    SaveStringToFile(Target, '{' + Json + '}', False)
  else if FileExists(Shipped) then
    FileCopy(Shipped, Target, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then WriteDefaults;
end;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  DataDir := ExpandConstant('{localappdata}\Pimmer');
  if not DirExists(DataDir) then Exit;
  if UninstallSilent then
  begin
    if HasParam('/REMOVEDATA') then DelTree(DataDir, True, True, True);
  end
  else if MsgBox('Also remove your Pimmer data (favorites, groups, history, settings)?',
                 mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
