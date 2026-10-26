; Footage Vault installer (Inno Setup 6). Built automatically by GitHub Actions.
; Installs for the current user only (no admin rights needed), adds a Start menu entry so the app shows in
; the app list and in Windows search, and registers an uninstaller under Settings > Apps.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6C1C3D0E-8D3B-4E0B-9C1F-5B6E9A2F7D41}
AppName=Footage Vault
AppVersion={#AppVersion}
AppVerName=Footage Vault {#AppVersion}
AppPublisher=Footage Vault
DefaultDirName={localappdata}\Programs\Footage Vault
DefaultGroupName=Footage Vault
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=FootageVault-Setup
SetupIconFile=..\src\FootageVault.App\Assets\FootageVault.ico
UninstallDisplayIcon={app}\FootageVault.exe
UninstallDisplayName=Footage Vault
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Footage Vault"; Filename: "{app}\FootageVault.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Footage Vault"; Filename: "{app}\FootageVault.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FootageVault.exe"; Description: "Open Footage Vault"; Flags: nowait postinstall skipifsilent
