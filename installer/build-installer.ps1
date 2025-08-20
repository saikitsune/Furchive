param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # App installer version (not .NET)
    [string]$Version = "1.0.0",
    # .NET Desktop Runtime version to bundle; use "auto" for latest in channel
    [string]$DotNetDesktopVersion = "auto",
    # .NET channel (major.minor)
    [string]$DotNetChannel = "8.0"
)

$ErrorActionPreference = "Stop"

function Get-DotNetDesktopDownloadInfo {
    param(
        [Parameter(Mandatory=$true)][string]$Channel,
        [Parameter(Mandatory=$true)][string]$RequestedVersion
    )

    $releasesUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/$Channel/releases.json"
    Write-Host "Resolving .NET Desktop Runtime from: $releasesUrl"
    try {
        $json = Invoke-RestMethod -UseBasicParsing -Uri $releasesUrl -Method Get -TimeoutSec 60
    }
    catch {
        throw "Failed to fetch releases metadata from $releasesUrl. $_"
    }

    if ($RequestedVersion -eq 'auto') {
        $targetVersion = $json.'latest-release'
    } else {
        $targetVersion = $RequestedVersion
    }

    $release = $json.releases | Where-Object { $_.version -eq $targetVersion } | Select-Object -First 1
    if (-not $release) {
        # Try fallback: pick latest release containing requested major.minor
        $release = $json.releases | Where-Object { $_.version -like "$Channel.*" } | Sort-Object { [version]$_.version } -Descending | Select-Object -First 1
    }
    if (-not $release) {
        throw "Could not locate release entry for version '$RequestedVersion' in channel '$Channel'"
    }

    $files = $release.windowsdesktop.files
    if (-not $files) { throw ".NET windowsdesktop files not found in releases metadata." }

    $file = $files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like 'windowsdesktop-runtime-*-win-x64.exe' } | Select-Object -First 1
    if (-not $file) { throw "Could not find win-x64 windowsdesktop runtime installer in metadata." }

    return [pscustomobject]@{
        Version = $release.version
        Url = $file.url
        FileName = $file.name
    }
}

# Paths
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $RepoRoot 'src/Furchive.Avalonia/Furchive.Avalonia.csproj'
$PublishDir = Join-Path $RepoRoot "src/Furchive.Avalonia/publish/$Runtime"
$InstallerDir = $PSScriptRoot
$RedistDir = Join-Path $InstallerDir 'redist'
$IssPath = Join-Path $InstallerDir 'Furchive.iss'
$InnoCompiler = ${env:INNO_SETUP_COMPILER}
if (-not $InnoCompiler) {
    # Common default install path
    $InnoCompiler = 'C:\\Program Files (x86)\\Inno Setup 6\\Compil32.exe'
}

# Resolve .NET Desktop Runtime download
try {
    $dotnetInfo = Get-DotNetDesktopDownloadInfo -Channel $DotNetChannel -RequestedVersion $DotNetDesktopVersion
}
catch {
    Write-Warning $_
    Write-Warning "Falling back to hardcoded .NET Desktop Runtime version 8.0.7 lookup via aka.ms thank-you link."
    $dotnetInfo = [pscustomobject]@{
        Version = '8.0.7'
        Url = 'https://dotnet.microsoft.com/download/dotnet/thank-you/runtime-desktop-8.0.7-windows-x64-installer'
        FileName = 'windowsdesktop-runtime-8.0.7-win-x64.exe'
    }
}

# Evergreen bootstrapper for WebView2 (installs latest)
$WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'

Write-Host "Publishing Furchive ($Configuration, $Runtime) to $PublishDir"
dotnet publish $Project -c $Configuration -r $Runtime --self-contained false -p:PublishSingleFile=false -p:PublishTrimmed=false -o $PublishDir | Out-Host

New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null

# Download prerequisites if missing
$DotNetInstaller = Join-Path $RedistDir $dotnetInfo.FileName
if (-not (Test-Path $DotNetInstaller)) {
    Write-Host "Downloading .NET Desktop Runtime $($dotnetInfo.Version)..."
    try {
        Invoke-WebRequest -UseBasicParsing -MaximumRedirection 5 -Uri $($dotnetInfo.Url) -OutFile $DotNetInstaller -TimeoutSec 600
    }
    catch {
        Write-Error "Failed to download .NET Desktop Runtime from $($dotnetInfo.Url). $_"
        throw
    }
}

$WebView2Installer = Join-Path $RedistDir 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
if (-not (Test-Path $WebView2Installer)) {
    Write-Host "Downloading WebView2 Evergreen bootstrapper..."
    Invoke-WebRequest -Uri $WebView2Url -OutFile $WebView2Installer
}

# Compile Inno Setup
if (-not (Test-Path $InnoCompiler)) {
    throw "Inno Setup compiler not found. Set INNO_SETUP_COMPILER env var or install Inno Setup 6."
}

# Ensure output directory exists
New-Item -ItemType Directory -Force -Path (Join-Path $InstallerDir 'output') | Out-Null

# Build with defines (as separate args)
$DefineArgs = @(
    "/DMyAppVersion=$Version",
    "/DDotNetDesktopVersion=$($dotnetInfo.Version)",
    "/DAppPublishDir=$PublishDir"
)

# Use ISCC.exe if available
$Iscc = 'C:\\Program Files (x86)\\Inno Setup 6\\ISCC.exe'
if (Test-Path $Iscc) {
    & $Iscc @DefineArgs $IssPath | Out-Host
}
else {
    # Compil32 expects options before the script path
    & $InnoCompiler "/cc" @DefineArgs $IssPath | Out-Host
}

Write-Host "Installer build completed. Output in: $(Join-Path $InstallerDir 'output')"
