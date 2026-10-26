; MicBoost installer (Inno Setup 6). Per-user install: no administrator rights needed.
; Build:  ISCC.exe /DAppVersion=1.0.0 /DSourceExe=..\publish\MicBoost.exe installer\MicBoost.iss

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\MicBoost.exe"
#endif

[Setup]
AppId={{B6F1C9D2-6E5A-4E0B-9B3C-2E8F2C7A4D11}
AppName=MicBoost
AppVersion={#AppVersion}
AppVerName=MicBoost {#AppVersion}
AppPublisher=Tuomas
AppPublisherURL=https://github.com/tuomix/micboost
AppSupportURL=https://github.com/tuomix/micboost/issues
AppUpdatesURL=https://github.com/tuomix/micboost/releases
DefaultDirName={localappdata}\Programs\MicBoost
DisableDirPage=yes
DefaultGroupName=MicBoost
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=MicBoost-Setup
SetupIconFile=..\assets\micboost.ico
UninstallDisplayIcon={app}\MicBoost.exe
UninstallDisplayName=MicBoost
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running app holds this mutex; setup asks the user to close MicBoost first.
AppMutex=MicBoost-SingleInstance-7f3c2a
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName=MicBoost

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start MicBoost when I sign in to Windows"; GroupDescription: "Options:"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MicBoost"; Filename: "{app}\MicBoost.exe"; Comment: "Boost your microphone volume"
Name: "{autodesktop}\MicBoost"; Filename: "{app}\MicBoost.exe"; Tasks: desktopicon

[Registry]
; Same entry the app's own "Start with Windows" switch writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MicBoost"; \
    ValueData: """{app}\MicBoost.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\MicBoost.exe"; Description: "{cm:LaunchProgram,MicBoost}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { The app may have added the startup entry itself, so always remove it. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MicBoost');

    SettingsDir := ExpandConstant('{userappdata}\MicBoost');
    if DirExists(SettingsDir) and not UninstallSilent then
    begin
      if MsgBox('Also delete your MicBoost settings and custom presets?' + #13#10#13#10 + SettingsDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(SettingsDir, True, True, True);
    end;
  end;
end;
