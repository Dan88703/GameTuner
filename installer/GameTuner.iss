; Inno Setup script for GameTuner.
; Built by .github/workflows/release.yml; can also be compiled locally:
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish /DOutputDir=..\artifacts GameTuner.iss

#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6B0E4F7A-3C59-4E8B-9C1D-2F7A5E6B9D10}
AppName=GameTuner
AppVersion={#AppVersion}
AppPublisher=Daniil Khlyshchankou
AppPublisherURL=https://github.com/Dan88703/GameTuner
DefaultDirName={localappdata}\Programs\GameTuner
DefaultGroupName=GameTuner
DisableProgramGroupPage=yes
; Per-user install: no admin rights needed to install.
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=GameTuner-{#AppVersion}-setup
SetupIconFile=..\src\GameTuner.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\GameTuner.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GameTuner"; Filename: "{app}\GameTuner.exe"
Name: "{autodesktop}\GameTuner"; Filename: "{app}\GameTuner.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GameTuner.exe"; Description: "{cm:LaunchProgram,GameTuner}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close a running GameTuner before files are removed.
Filename: "taskkill.exe"; Parameters: "/IM GameTuner.exe /F"; Flags: runhidden; RunOnceId: "KillGameTuner"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\GameTuner\tools"

[Registry]
; Remove the "start with Windows" entry on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "GameTuner"; Flags: uninsdeletevalue dontcreatekey
