param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    # App installer version (not .NET)
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

function noop { }

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


Write-Host "Publishing Furchive ($Configuration, $Runtime) to $PublishDir"
dotnet publish $Project -c $Configuration -r $Runtime --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o $PublishDir | Out-Host

New-Item -ItemType Directory -Force -Path $RedistDir | Out-Null

# (WebView2 runtime previously downloaded here; removed because app no longer embeds WebView.)

# Compile Inno Setup
if (-not (Test-Path $InnoCompiler)) {
    throw "Inno Setup compiler not found. Set INNO_SETUP_COMPILER env var or install Inno Setup 6."
}

# Ensure output directory exists
New-Item -ItemType Directory -Force -Path (Join-Path $InstallerDir 'output') | Out-Null

# Build with defines (as separate args)
$DefineArgs = @(
    "/DMyAppVersion=$Version",
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