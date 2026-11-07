; Inno Setup 6 script — per-user install, no admin rights. Built by build.ps1 when ISCC.exe is available.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\AirPodsControl-portable"
#endif

[Setup]
AppId={{6C5E1A52-8E0B-4E39-9E9B-3A4F2B1C7D11}
AppName=AirPods Control
AppVersion={#AppVersion}
AppPublisher=AirPods Control (open source)
DefaultDirName={localappdata}\Programs\AirPodsControl
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=AirPodsControl-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
SetupIconFile=..\src\AirPodsControl.App\Assets\app.ico
UninstallDisplayIcon={app}\AirPodsControl.exe
WizardStyle=modern

[Tasks]
Name: "autostart"; Description: "Start AirPods Control with Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\AirPods Control"; Filename: "{app}\AirPodsControl.exe"
Name: "{autodesktop}\AirPods Control"; Filename: "{app}\AirPodsControl.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AirPodsControl"; \
  ValueData: """{app}\AirPodsControl.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\AirPodsControl.exe"; Description: "Launch AirPods Control"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /im AirPodsControl.exe /f"; Flags: runhidden; RunOnceId: "KillApp"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\AirPodsControl"
