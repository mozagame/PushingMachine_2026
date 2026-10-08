; Inno Setup 6 script. Build after publish.ps1:  iscc /DVersion=2.0.0 build\installer.iss
#ifndef Version
  #define Version "2.0.0"
#endif
#define Src "..\artifacts\CheckBarcode-" + Version + "-win-x64"

[Setup]
AppId={{6C1E4B6A-2F0B-4C7E-9B8D-CB2A0D0C2B01}
AppName=CheckBarcode
AppVersion={#Version}
AppPublisher=Apillis
DefaultDirName=C:\CheckBarcode
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=..\artifacts
OutputBaseFilename=CheckBarcode-{#Version}-setup
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#Src}\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion; Excludes: "config\*"
; Configuration is installed once and never overwritten (change-controlled on site; hash is audited at start-up).
Source: "{#Src}\app\config\*"; DestDir: "{app}\config"; Flags: recursesubdirs onlyifdoesntexist uninsneveruninstall
Source: "{#Src}\PlcSimulator\*"; DestDir: "{app}\PlcSimulator"; Flags: recursesubdirs ignoreversion
Source: "{#Src}\docs\*"; DestDir: "{app}\docs"; Flags: recursesubdirs ignoreversion
Source: "{#Src}\SHA256SUMS.txt"; DestDir: "{app}"

[Dirs]
; Data folder (paths in config are relative to the exe). The application runs as the Windows kiosk user,
; so that account needs modify rights here; protect it by restricting the Windows account (docs/DEPLOYMENT.md).
; Tampering outside the application is detected by the SHA-256 hash chain (INTEGRITY_CHECK at start-up).
Name: "{app}\data"; Permissions: users-modify; Flags: uninsneveruninstall

[Icons]
Name: "{commondesktop}\CheckBarcode"; Filename: "{app}\CheckBarcode.exe"
Name: "{commonstartup}\CheckBarcode"; Filename: "{app}\CheckBarcode.exe"

[UninstallDelete]
; Data (database, audit trail, images, reports) is intentionally kept on uninstall (record retention).
