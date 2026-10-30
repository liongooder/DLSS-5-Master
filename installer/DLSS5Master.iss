; DLSS 5 Master installer (Inno Setup 6). Build with build-installer.ps1.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppName "DLSS 5 Master"
#define AppExe "DLSS5Master.exe"
#define PublishDir "..\publish"

[Setup]
AppId={{6F1C2A9E-5D3B-4C8E-9A1F-D55A5E0C7B21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Liongooder
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default; the user can choose "all users" (needs admin) on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=DLSS5Master-Setup-{#AppVersion}
SetupIconFile=..\src\DLSS5Master\Assets\DLSS5Master.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dark
WizardImageFile=WizardImage164.bmp,WizardImage192.bmp,WizardImage246.bmp,WizardImage273.bmp,WizardImage328.bmp,WizardImage355.bmp,WizardImage410.bmp
WizardSmallImageFile=WizardSmall55.bmp,WizardSmall64.bmp,WizardSmall83.bmp,WizardSmall92.bmp,WizardSmall110.bmp,WizardSmall119.bmp,WizardSmall138.bmp
WizardImageStretch=no
WizardImageAlphaFormat=defined
#ifdef PreviewWelcome
DisableWelcomePage=no
#endif
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany=Liongooder
VersionInfoCopyright=Copyright (c) 2026 Liongooder
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion

[InstallDelete]
; Shortcuts from versions named "DLSS5 Master" (without the space).
Type: files; Name: "{autoprograms}\DLSS5 Master.lnk"
Type: files; Name: "{autodesktop}\DLSS5 Master.lnk"
; Updating from 0.5.x (.NET 8): remove that runtime's DLLs (all DLLs are installed again below) and the OptiScaler
; zip stored under its old name, so nothing outdated stays behind in the install folder.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\payload\optiscaler\OptiScaler-NR-v0.8.3-rtx40-mfg.zip"
; 0.8.1 bundles OptiScaler NR 0.9.72 instead of 0.8.3, which is now downloaded when first chosen.
Type: files; Name: "{app}\payload\optiscaler\OptiScaler-0_8_3-rtx40-mfg.zip"
; 0.8.2 bundles OptiScaler NR 0.9.80; remove the 0.9.72 zip it replaces.
Type: files; Name: "{app}\payload\optiscaler\OptiScaler-0_9_72-shyvortex.zip"
; 0.9.4 bundles OptiScaler NR 0.9.81; remove the 0.9.80 zip it replaces.
Type: files; Name: "{app}\payload\optiscaler\OptiScaler-0_9_80-shyvortex.zip"

[UninstallDelete]
; The large caches in the app's data folder (about 1.2 GB on a used install): downloaded add-ons and OptiScaler builds,
; the overlay's working copies and a downloaded app update. Settings, the activity log, the community install ID and
; artwork stay, so a reinstall keeps them.
Type: filesandordirs; Name: "{localappdata}\DLSS5Master\components"
Type: filesandordirs; Name: "{localappdata}\DLSS5Master\overlay"
Type: filesandordirs; Name: "{localappdata}\DLSS5Master\app-update"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; In-app updates run this installer with /SILENT: start the new version again afterwards, without the administrator
; rights an all-users install runs with (an app started as administrator cannot open Windows' file pickers).
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
// Setup runs from a .tmp file, which Windows' taskbar shows with a blank program icon. Giving the wizard window
// its own app ID and icon (taken from this setup .exe) makes the taskbar show the DLSS 5 Master logo instead.
type
  TPropertyKey = record
    FmtId: TGUID;
    Pid: DWORD;
  end;
  TPropVariant = record
    Vt: Word;
    Reserved1, Reserved2, Reserved3: Word;
    Value, Value2: Longint;
  end;
  IPropertyStore = interface(IUnknown)
    '{886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}'
    function GetCount(out Count: DWORD): HResult;
    function GetAt(Index: DWORD; out Key: TPropertyKey): HResult;
    function GetValue(const Key: TPropertyKey; out Value: TPropVariant): HResult;
    function SetValue(const Key: TPropertyKey; const Value: TPropVariant): HResult;
    function Commit: HResult;
  end;

function SHGetPropertyStoreForWindow(Wnd: HWND; const Iid: TGUID; out Store: IPropertyStore): HResult;
  external 'SHGetPropertyStoreForWindow@shell32.dll stdcall';

var
  // Kept alive while Setup runs: the window properties point at these strings until Commit copies them.
  TaskbarId, TaskbarIcon, TaskbarCommand, TaskbarName: String;

procedure SetWindowString(Store: IPropertyStore; Pid: DWORD; var Text: String);
var
  Key: TPropertyKey;
  Value: TPropVariant;
begin
  Key.FmtId := StringToGUID('{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}');   // PKEY_AppUserModel_*
  Key.Pid := Pid;
  Value.Vt := 31;   // VT_LPWSTR
  Value.Value := CastStringToInteger(Text);
  OleCheck(Store.SetValue(Key, Value));
end;

procedure InitializeWizard();
var
  Store: IPropertyStore;
begin
  TaskbarId := 'Liongooder.DLSS5Master.Setup';
  TaskbarIcon := ExpandConstant('{srcexe}') + ',0';
  TaskbarCommand := '"' + ExpandConstant('{srcexe}') + '"';
  TaskbarName := '{#AppName} Setup';
  try
    OleCheck(SHGetPropertyStoreForWindow(WizardForm.Handle, StringToGUID('{886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}'), Store));
    SetWindowString(Store, 5, TaskbarId);        // ID
    SetWindowString(Store, 2, TaskbarCommand);   // RelaunchCommand
    SetWindowString(Store, 3, TaskbarIcon);      // RelaunchIconResource
    SetWindowString(Store, 4, TaskbarName);      // RelaunchDisplayNameResource
    OleCheck(Store.Commit);
  except
    Log('Taskbar icon not set: ' + GetExceptionMessage);
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := SuppressibleMsgBox('Uninstalling DLSS 5 Master does not undo changes it made to your games.' + #13#10 + #13#10 +
    'If you want a game back to its original files, cancel now, open DLSS 5 Master and use "Restore originals" on that game first.' + #13#10 + #13#10 +
    'Continue uninstalling?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\DLSS5Master');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Also delete DLSS 5 Master settings, downloaded components and the DLL library?' + #13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
