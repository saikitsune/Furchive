#define AppName "Furchive"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Application files (published output)
Source: "{#AppPublishDir}\\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; WebView2 prerequisite copied to temporary folder and removed after install
Source: "redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsWebView2Installed

[Icons]
Name: "{autoprograms}\\{#AppName}"; Filename: "{app}\\Furchive.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\\{#AppName}"; Filename: "{app}\\Furchive.exe"; Tasks: desktopicon

[Run]
; Install WebView2 Evergreen Runtime if missing
Filename: "{tmp}\\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/silent /install"; StatusMsg: "Microsoft Edge WebView2 Runtime is required. Installing..."; Check: not IsWebView2Installed; Flags: waituntilterminated

; Launch application after install (optional)
Filename: "{app}\\Furchive.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Checks if WebView2 Evergreen Runtime is installed by checking its application directory
function IsWebView2Installed: Boolean;
var
  PathX86: string;
  PathX64: string;
begin
  // WebView2 installs to Program Files (x86) regardless of OS bitness for the Evergreen runtime
  PathX86 := ExpandConstant('{pf32}') + '\\Microsoft\\EdgeWebView\\Application';
  PathX64 := ExpandConstant('{pf}') + '\\Microsoft\\EdgeWebView\\Application';
  Result := DirExists(PathX86) or DirExists(PathX64);
end;
