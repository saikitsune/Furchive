#define AppName "Furchive"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.0.0"
#endif
#ifndef AppPublishDir
  #define AppPublishDir "..\\src\\Furchive.Avalonia\\publish\\win-x64"
#endif

[Setup]
AppId={{1E7B7A7F-8D8F-4E2A-9C6B-8E2A44F1F9C1}}
AppName={#AppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\\Programs\\{#AppName}
AppPublisherURL=https://github.com/saikitsune/Furchive
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64
OutputDir=output
OutputBaseFilename=Furchive-Setup
PrivilegesRequired=lowest
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
DisableDirPage=no
UninstallDisplayIcon={app}\\Furchive.exe
SetupIconFile=..\assets\icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Application files (published output)
; Use the resolved AppPublishDir (defaults to win-x64 publish folder). The previous wildcard to the parent
; publish directory caused a build failure when that folder only contained subdirectories (no direct files).
; We now rely solely on the win-x64 (or overridden) publish output and recurse into it.
Source: "{#AppPublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs



[Icons]
Name: "{autoprograms}\\{#AppName}"; Filename: "{app}\\Furchive.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\\{#AppName}"; Filename: "{app}\\Furchive.exe"; Tasks: desktopicon

[Run]
; Launch application after install (optional)
Filename: "{app}\\Furchive.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent


