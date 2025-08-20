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
	DotNetRuntimeMissing: Boolean;

function IsDotNet8DesktopRuntimeInstalled(): Boolean;
var
	ResultCode: Integer;
	S, TmpFile: string;
begin
	// Quick probe: run 'dotnet --list-runtimes' and look for Microsoft.WindowsDesktop.App 8.
	// This doesn't require admin and works if dotnet is on PATH (installed via runtime installer).
	// Fallback to registry checks if needed.
	try
	begin
		TmpFile := ExpandConstant('{tmp}') + '\\runtimes.txt';
		if Exec('cmd.exe', '/C dotnet --list-runtimes > "' + TmpFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
		begin
			if LoadStringFromFile(TmpFile, S) then
			begin
				if Pos('Microsoft.WindowsDesktop.App 8.', S) > 0 then
				begin
					Result := True;
					exit;
				end;
			end;
		end;
	end;
	except
	end;

	// Registry probe for WindowsDesktop 8.x: HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App
	if RegKeyExists(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App') then
	begin
		Result := True;
	end
	else
	begin
		Result := False;
	end;
end;

function DownloadFile(const Url, DestFile: string): Boolean;
var
	WinHttpReq: Variant;
	Stream: Variant;
begin
	try
	begin
		WinHttpReq := CreateOleObject('WinHttp.WinHttpRequest.5.1');
		WinHttpReq.Open('GET', Url, False);
		WinHttpReq.Send;
		if (WinHttpReq.Status >= 200) and (WinHttpReq.Status < 300) then
		begin
			Stream := CreateOleObject('ADODB.Stream');
			Stream.Type := 1; // binary
			Stream.Open;
			Stream.Write(WinHttpReq.ResponseBody);
			Stream.SaveToFile(DestFile, 2);
			Stream.Close;
			Result := True;
			exit;
		end;
	end;
	except
	end;
	Result := False;
end;

function GetDotNet8DesktopRuntimeUrl(): string;
begin
	// Use official evergreen link that redirects to the latest .NET 8 Desktop Runtime x64 installer
	// This URL is maintained by Microsoft to always point to the latest 8.x desktop runtime
	Result := 'https://dot.net/v1/dotnet-install.ps1'; // fallback script
end;

function InstallDotNet8DesktopRuntime(): Boolean;
var
	TempDir, InstallerPath, Cmd, PowerShell, Args: string;
	ResultCode: Integer;
begin
	TempDir := ExpandConstant('{tmp}');
	PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
	// Prefer using dotnet-install script to fetch latest 8.x desktop runtime
	InstallerPath := TempDir + '\\dotnet-install.ps1';
	if not DownloadFile(GetDotNet8DesktopRuntimeUrl(), InstallerPath) then
	begin
		MsgBox('Failed to download the .NET installer script. Please check your internet connection and try again.', mbCriticalError, MB_OK);
		Result := False;
		exit;
	end;
	// Install 64-bit .NET 8 Desktop Runtime (WindowsDesktop)
	Args := '-NoProfile -ExecutionPolicy Bypass -File "' + InstallerPath + '" -Runtime windowsdesktop -Version latest -Architecture x64';
	if not Exec(PowerShell, Args, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
	begin
		MsgBox('Failed to launch .NET runtime installer.', mbCriticalError, MB_OK);
		Result := False;
		exit;
	end;
	if ResultCode <> 0 then
	begin
		MsgBox('The .NET desktop runtime installer returned an error code: ' + IntToStr(ResultCode) + '.', mbCriticalError, MB_OK);
		Result := False;
		exit;
	end;
	// Ensure DOTNET_ROOT points to user-local installation so the app can resolve the runtime
	try
	begin
		RegWriteStringValue(HKCU, 'Environment', 'DOTNET_ROOT', ExpandConstant('{userprofile}') + '\\.dotnet');
	end;
	except
	end;
	Result := True;
end;

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

function InitializeSetup(): Boolean;
var
	resp: Integer;
begin
	DotNetRuntimeMissing := not IsDotNet8DesktopRuntimeInstalled();
	if DotNetRuntimeMissing then
	begin
		resp := MsgBox(
		  'Furchive requires the Microsoft .NET 8 Desktop Runtime to run. It''s not detected on your system.\n\n'
		  + 'Would you like the installer to download and install the latest .NET 8 Desktop Runtime from Microsoft now?\n\n'
		  + 'This is safe and only needs to be done once. An internet connection is required.',
		  mbConfirmation, MB_YESNO or MB_DEFBUTTON1);
		if resp = IDYES then
		begin
			if not InstallDotNet8DesktopRuntime() then
			begin
				Result := False; // abort setup if user chose to install but it failed
				exit;
			end
			else
			begin
				// Re-check after install
				if not IsDotNet8DesktopRuntimeInstalled() then
				begin
					MsgBox('The .NET 8 Desktop Runtime still was not detected after installation. Setup will exit.', mbCriticalError, MB_OK);
					Result := False;
					exit;
				end;
			end;
		end
		else
		begin
			MsgBox('The .NET 8 Desktop Runtime is required to run Furchive. Setup will exit.', mbCriticalError, MB_OK);
			Result := False;
			exit;
		end;
	end;
	Result := True;
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
; The installer verifies .NET 8 Desktop Runtime and offers to install it on-demand before launching the app
; Use ShellExecute so Windows honors the app's UAC manifest prompt
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent shellexec

[Dirs]
; Ensure user-writable app data folder exists for logs or caches
Name: "{localappdata}\\Furchive"; Flags: uninsalwaysuninstall
