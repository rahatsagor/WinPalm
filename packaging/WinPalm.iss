#ifndef SourceDir
  #error SourceDir must point to a complete scripts/build.ps1 output.
#endif
#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\winpalm-dev\artifacts"
#endif
#define AppVersion GetFileVersion(SourceDir + "\WinPalm.exe")
[Setup]
AppId={{BA164719-209A-4D5B-8B2F-1D22859FA5AF}
AppName=WinPalm
AppVersion={#AppVersion}
AppPublisher=RS Application
DefaultDirName={localappdata}\Programs\WinPalm
DefaultGroupName=WinPalm
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=WinPalm-{#AppVersion}-{#Architecture}-Setup
SetupIconFile=..\windows\WinPalm\Resources\WinPalm.ico
UninstallDisplayIcon={app}\WinPalm.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
MinVersion=10.0.17763
#if Architecture == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Launch WinPalm when I sign in"; Flags: unchecked
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\WinPalm"; Filename: "{app}\WinPalm.exe"
Name: "{autodesktop}\WinPalm"; Filename: "{app}\WinPalm.exe"; Tasks: desktopicon
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WinPalm"; ValueData: """{app}\WinPalm.exe"" --start-minimized"; Flags: uninsdeletevalue; Tasks: startup
; WinPalm's own "Launch when I sign in" setting writes the same value; remove it on uninstall either way.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "WinPalm"; Flags: uninsdeletevalue dontcreatekey
[Run]
Filename: "{app}\WinPalm.exe"; Description: "Open WinPalm"; Flags: nowait postinstall skipifsilent
; Paired devices, preferences and decks are deliberately preserved on uninstall.
; Firewall access is granted by the owner through Windows, on Private networks.
