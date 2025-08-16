; Inno Setup script for Furchive
#define AppName "Furchive"
#define AppVersion "1.0.5"
#define AppPublisher "Furchive"
#define AppExeName "Furchive.exe"

[Setup]
AppId={{4B1C5C5E-7E98-4B54-92B0-551F7AA7B3B0}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; Install per-user by default (no admin), under %LocalAppData%\Programs
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=FurchiveSetup
Compression=lzma
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
DisableProgramGroupPage=yes
; Enforce per-user install only (no all-users option)
PrivilegesRequired=lowest
SetupLogging=yes
SetupIconFile=..\..\assets\icon.ico
Uninstallable=yes
; Add a checkbox on the final uninstall page to remove user settings
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Files are copied from the publish directory built by dotnet publish
Source: "..\..\src\Furchive\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Include the WebView2 Evergreen bootstrapper downloaded by the build script
Source: "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[UninstallDelete]
; Always clean up debug artifacts if present
Type: filesandordirs; Name: "{app}\bin\Debug"
Type: files; Name: "{app}\debug.log"
; Offer to remove the entire app folder (safety: after files are removed)
Type: filesandordirs; Name: "{app}"

[Code]
var
	RemoveUserSettings: Boolean;

function InitializeUninstall(): Boolean;
var
	resp: Integer;
begin
	// Prompt up front, before any files are removed
	RemoveUserSettings := False;
	resp := MsgBox('Do you also want to remove all user settings (cache, temp, and settings.json)?',
		mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
	RemoveUserSettings := (resp = IDYES);
	Result := True; // continue with uninstall
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
	userData: string;
begin
	if (CurUninstallStep = usUninstall) and RemoveUserSettings then
	begin
		userData := ExpandConstant('{localappdata}') + '\\Furchive';
		if DirExists(userData) then
		begin
			DelTree(userData, True, True, True);
		end;
	end;
end;

[Run]
; Install WebView2 runtime silently (idempotent)
Filename: "{tmp}\\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"; Parameters: "/install /quiet /norestart /log ""{localappdata}\\Furchive\\webview2-install.log"""; Flags: waituntilterminated runhidden; Check: NeedsWebView2
; Use ShellExecute so Windows honors the app's UAC manifest prompt
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent shellexec

[Dirs]
; Ensure user-writable app data folder exists for logs or caches
Name: "{localappdata}\\Furchive"; Flags: uninsalwaysuninstall

[Code]
function NeedsWebView2(): Boolean;
var
	F: TFindRec;
	base: string;
begin
	// Known install roots (folders exist when installed)
	if DirExists(ExpandConstant('{pf32}\\Microsoft\\EdgeWebView\\Application')) then begin Result := False; exit; end;
	if DirExists(ExpandConstant('{pf64}\\Microsoft\\EdgeWebView\\Application')) then begin Result := False; exit; end;
	if DirExists(ExpandConstant('{localappdata}\\Microsoft\\EdgeWebView\\Application')) then begin Result := False; exit; end;

	// Robust check: look for msedgewebview2.exe in versioned subfolders
	base := ExpandConstant('{pf32}') + '\\Microsoft\\EdgeWebView\\Application\\*\\msedgewebview2.exe';
	if FindFirst(base, F) then begin FindClose(F); Result := False; exit; end;
	base := ExpandConstant('{pf64}') + '\\Microsoft\\EdgeWebView\\Application\\*\\msedgewebview2.exe';
	if FindFirst(base, F) then begin FindClose(F); Result := False; exit; end;
	base := ExpandConstant('{localappdata}') + '\\Microsoft\\EdgeWebView\\Application\\*\\msedgewebview2.exe';
	if FindFirst(base, F) then begin FindClose(F); Result := False; exit; end;

	// Not found -> need to install
	Result := True;
end;
