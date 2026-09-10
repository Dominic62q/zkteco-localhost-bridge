; Fingerprint Bridge — generic installer for any authorized web app.
; Packages the self-contained bridge.exe (no .NET needed on target).
; Requires the ZKTeco driver (libzkfp.dll) already installed via ZKFinger setup.
; Build: ISCC.exe installer\bridge-setup.iss  (from the project root)
#define MyAppName "Fingerprint Bridge"
#define MyAppExeName "bridge.exe"
#define MyAppVersion "1.3.0"
[Setup]
AppId={{B7E8F2A1-4C6D-4E9B-8F1A-2C3D4E5F6071}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Local install
DefaultDirName={autopf}\FingerprintBridge
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=FingerprintBridgeSetup-{#MyAppVersion}
ArchitecturesAllowed=x86compatible
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName}

[Files]
Source: "..\\dist\\bridge\\bridge.exe"; DestDir: "{app}\\bin"; Flags: ignoreversion
; appsettings.json (open universal config) is generated per install by [Code] below.

[Dirs]
Name: "{app}\\data"

[Icons]
Name: "{autoprograms}\{#MyAppName}\Fingerprint Bridge"; Filename: "{app}\\bin\\{#MyAppExeName}"
Name: "{autoprograms}\{#MyAppName}\Uninstall"; Filename: "{uninstallexe}"

[Tasks]
Name: "startup"; Description: "Start the bridge when Windows starts"

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FingerprintBridge"; ValueData: """{app}\\bin\\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\\bin\\{#MyAppExeName}"; Flags: nowait postinstall skipifsilent; Description: "Start the bridge now"

[UninstallDelete]
; Generated per install. Biometric data in {app}\data is
; deliberately kept — delete it manually to remove all biometric data.
Type: files; Name: "{app}\bin\appsettings.json"
[Code]
function DriverPresent(): Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\libzkfp.dll'))
    or FileExists(ExpandConstant('{syswow64}\libzkfp.dll'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not DriverPresent() then
  begin
    if MsgBox('ZKTeco fingerprint driver (libzkfp.dll) was not found.' + #13#10 + #13#10 +
      'Install the ZKFinger SDK driver package first, then re-run this setup.' + #13#10 +
      'Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

procedure WriteBridgeConfig();
var
  Json: string;
begin
  Json :=
    '{' + #13#10 +
    '  "DataPath": "..\\data\\fingerprint.db",' + #13#10 +
    '  "Bridge": {' + #13#10 +
    '    "AllowedOrigins": ["*"],' + #13#10 +
    '    "ApiTokens": []' + #13#10 +
    '  }' + #13#10 +
    '}';
  SaveStringToFile(ExpandConstant('{app}\bin\appsettings.json'), Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteBridgeConfig();
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption :=
      'Fingerprint Bridge is installed.' + #13#10 + #13#10 +
      'The bridge accepts any page origin — no domain setup needed.' + #13#10 +
      'Config lives in:' + #13#10 +
      ExpandConstant('{app}\bin\appsettings.json') + #13#10 +
      'Biometric data stays in the data folder on uninstall.';
end;
