; Compilare con:
;   dotnet publish src\GpsBridge.App -c Release -r win-x64 --self-contained -o installer\publish
;   ISCC installer\GpsBridge.iss        (Inno Setup 6)
[Setup]
AppName=GpsBridge
AppVersion=1.0
AppPublisher=GpsBridge contributors
DefaultDirName={autopf}\GpsBridge
DefaultGroupName=GpsBridge
OutputDir=output
OutputBaseFilename=GpsBridge-setup
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
LicenseFile=..\LICENSE

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\GpsBridge"; Filename: "{app}\GpsBridge.App.exe"
Name: "{autodesktop}\GpsBridge"; Filename: "{app}\GpsBridge.App.exe"

[Run]
Filename: "{app}\GpsBridge.App.exe"; Description: "Avvia GpsBridge"; Flags: postinstall nowait skipifsilent
