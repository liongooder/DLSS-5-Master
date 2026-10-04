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

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; In-app updates run this installer with /SILENT: start the new version again afterwards.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent

[Code]
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
