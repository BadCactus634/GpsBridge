; Compilare con tools\build-installer.ps1 (pubblica l'app e lancia ISCC passando la versione)
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{B7A1F0C2-6E3D-4C59-9A41-3D2F8E5B7C10}
AppName=GpsBridge
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Stefano Rossi
AppPublisherURL=https://github.com/BadCactus634/GpsBridge
AppSupportURL=https://github.com/BadCactus634/GpsBridge/issues
AppUpdatesURL=https://github.com/BadCactus634/GpsBridge/releases
AppCopyright=Copyright (c) 2026 Stefano Rossi
DefaultDirName={autopf}\GpsBridge
DefaultGroupName=GpsBridge
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=GpsBridge-setup
SetupIconFile=..\src\GpsBridge.App\Assets\icon\icon.ico
UninstallDisplayIcon={app}\GpsBridge.App.exe
LicenseFile=..\LICENSE
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; l'app tiene questo mutex finche' e' aperta: l'installer la chiude (o aspetta) prima di sostituire i file
AppMutex=GpsBridgeSingleInstance
CloseApplications=yes

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\GpsBridge"; Filename: "{app}\GpsBridge.App.exe"
Name: "{autodesktop}\GpsBridge"; Filename: "{app}\GpsBridge.App.exe"; Tasks: desktopicon

[Registry]
; l'app scrive qui se si attiva «Avvia con Windows»: alla disinstallazione si toglie
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "GpsBridge"; Flags: dontcreatekey uninsdeletevalue

[Run]
; installazione normale: casella «Avvia GpsBridge» a fine procedura
Filename: "{app}\GpsBridge.App.exe"; Description: "{cm:LaunchProgram,GpsBridge}"; Flags: postinstall nowait skipifsilent runasoriginaluser
; aggiornamento dall'app (silenzioso): riparte da sola
Filename: "{app}\GpsBridge.App.exe"; Parameters: "/RELAUNCH=1"; Flags: nowait runasoriginaluser; Check: IsRelaunch

[Code]
function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;
