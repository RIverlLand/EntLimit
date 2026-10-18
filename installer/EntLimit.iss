#ifndef PayloadDir
  #define PayloadDir "..\artifacts\release-payload"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\artifacts\installer"
#endif
[Setup]
AppId={{9BA69C1B-234D-480B-BCC4-07CBB798E0DC}
AppName=EntLimit
AppVersion=0.3.1
AppPublisher=EntLimit contributors
DefaultDirName={autopf}\EntLimit
DefaultGroupName=EntLimit
DisableDirPage=yes
UsePreviousAppDir=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#ReleaseDir}
OutputBaseFilename=EntLimit-Setup-0.3.1-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\EntLimit.Desktop.exe
CloseApplications=yes
RestartApplications=no
SetupLogging=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcuts for EntLimit and Emergency Stop"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "data\*,*.pdb,*.log,*.tmp,*.dpapi,api-settings.json,installed-plugins.user.json"

[Icons]
Name: "{group}\EntLimit"; Filename: "{app}\EntLimit.Desktop.exe"; WorkingDir: "{app}"
Name: "{group}\Emergency Stop - EntLimit"; Filename: "{app}\EntLimit.EmergencyStop.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall EntLimit"; Filename: "{uninstallexe}"
Name: "{autodesktop}\EntLimit"; Filename: "{app}\EntLimit.Desktop.exe"; Tasks: desktopicon
Name: "{autodesktop}\Emergency Stop - EntLimit"; Filename: "{app}\EntLimit.EmergencyStop.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EntLimit.Desktop.exe"; Description: "Open EntLimit (normal user permissions)"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\EntLimit.EmergencyStop.exe"; Parameters: "--quiet"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "StopEntLimit"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
