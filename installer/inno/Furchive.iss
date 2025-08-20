; Inno Setup script for Furchive
#define AppName "Furchive"
#ifndef AppVersion
#define AppVersion "1.1.0.0"
#endif
#define AppPublisher "Furchive"
#define AppExeName "Furchive.exe"

[Setup]
AppId={{4B1C5C5E-7E98-4B54-92B0-551F7AA7B3B0}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
; Install per-user by default (no admin), under %LocalAppData%\Programs
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
OutputDir=output
OutputBaseFilename=FurchiveSetup-{#AppVersion}
Compression=lzma
SolidCompression=yes
; Use the modern architecture identifier to avoid deprecation warnings
ArchitecturesInstallIn64BitMode=x64os
DisableProgramGroupPage=yes
; Enforce per-user install only (no all-users option)
PrivilegesRequired=lowest
SetupLogging=yes
; SetupIconFile=..\..\assets\icon.ico
Uninstallable=yes
; Add a checkbox on the final uninstall page to remove user settings
; UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Files are copied from the Avalonia publish directory built by dotnet publish
Source: "..\..\src\Furchive.Avalonia\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

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
; .NET runtime is bundled via self-contained publish, no need to bootstrap runtime separately
; Use ShellExecute so Windows honors the app's UAC manifest prompt
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent shellexec

[Dirs]
; Ensure user-writable app data folder exists for logs or caches
Name: "{localappdata}\\Furchive"; Flags: uninsalwaysuninstall
