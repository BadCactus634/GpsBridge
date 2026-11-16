; Compilare con: dotnet publish src\GpsBridge.App -c Release -r win-x64 --self-contained -o installer\publish
;   poi ISCC installer\GpsBridge.iss
; Per includere com0com copiare qui installer\com0com\ (setupc.exe, com0com.sys, com0com.inf, com0com.cat, cncport.inf, ...)
; dalla versione firmata scaricata da https://sourceforge.net/projects/com0com/files/com0com/
[Setup]
AppName=GpsBridge
AppVersion=1.0
DefaultDirName={autopf}\GpsBridge
DefaultGroupName=GpsBridge
OutputDir=output
OutputBaseFilename=GpsBridge-setup
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
#ifexist "com0com\setupc.exe"
Source: "com0com\*"; DestDir: "{app}\com0com"; Flags: recursesubdirs ignoreversion
#endif

[Icons]
Name: "{group}\GpsBridge"; Filename: "{app}\GpsBridge.App.exe"
Name: "{autodesktop}\GpsBridge"; Filename: "{app}\GpsBridge.App.exe"

[Run]
#ifexist "com0com\setupc.exe"
Filename: "{app}\com0com\setupc.exe"; Parameters: "install PortName=COM20 PortName=COM21"; WorkingDir: "{app}\com0com"; StatusMsg: "Installo la coppia di porte COM virtuali..."; Flags: runhidden
#endif
Filename: "{app}\GpsBridge.App.exe"; Description: "Avvia GpsBridge"; Flags: postinstall nowait skipifsilent

[UninstallRun]
#ifexist "com0com\setupc.exe"
Filename: "{app}\com0com\setupc.exe"; Parameters: "uninstall"; WorkingDir: "{app}\com0com"; Flags: runhidden
#endif
