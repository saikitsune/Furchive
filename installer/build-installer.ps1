param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  # Version is optional; when omitted we derive it from the csproj FileVersion/AssemblyVersion
  [string]$Version,
  # Optional: override the download URL for the WebView2 runtime (e.g., to a GitHub Release asset)
  [string]$WebView2Url,
  # Optional: override the download URL for the .NET Desktop Runtime (x64) installer
  [string]$DotNetDesktopUrl
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
$appProj = Join-Path $repo 'src/Furchive.Avalonia/Furchive.Avalonia.csproj'
$publishDir = Join-Path $repo 'src/Furchive.Avalonia/publish'
$iss = Join-Path $root 'inno/Furchive.iss'
${outDir} = Join-Path $root 'inno/output'

# Determine effective version from the csproj if not supplied (or if supplied value doesn't match project)
function Get-ProjectVersion([string]$csprojPath) {
  try {
    [xml]$xml = Get-Content -Path $csprojPath -ErrorAction Stop
    $fileVer = ($xml.Project.PropertyGroup | ForEach-Object { $_.FileVersion } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $fileVer) { $fileVer = ($xml.Project.PropertyGroup | ForEach-Object { $_.AssemblyVersion } | Where-Object { $_ } | Select-Object -First 1) }
    if (-not $fileVer) { return '1.0.0' }
    $ver = ($fileVer | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($ver)) { return '1.0.0' }
  # Use full version as-is (supports 4-part), suitable for VersionInfo and output filename
  return $ver
  } catch { return '1.0.0' }
}

$projectVersion = Get-ProjectVersion $appProj
$effectiveVersion = $projectVersion
if (-not [string]::IsNullOrWhiteSpace($Version) -and ($Version -ne $projectVersion)) {
  Write-Warning "Provided -Version '$Version' differs from project version '$projectVersion'. Will use the app binary version to keep installer in sync."
}

# Ensure clean publish directory to avoid stale files
if (Test-Path $publishDir) {
  Write-Host "Cleaning publish directory $publishDir ..."
  Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "Publishing app ($Configuration, $Runtime) to $publishDir..."
# Do not override version at publish time; let the project control the binary version
& dotnet publish $appProj -c $Configuration -r $Runtime --self-contained true -p:PublishTrimmed=false -o $publishDir

# After publish, read the actual app version from the produced EXE to ensure exact alignment
# Try to locate the primary executable (prefer Furchive.exe if present, otherwise first *.exe)
$exePath = Join-Path $publishDir 'Furchive.exe'
if (-not (Test-Path $exePath)) {
  $candidates = Get-ChildItem -Path $publishDir -Filter '*.exe' -File | Select-Object -First 1
  if ($candidates) { $exePath = $candidates.FullName } else { throw "Published app executable not found in $publishDir" }
}
try {
  $fvi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
  $binVer = $null
  if ($fvi.FileVersion) { $binVer = $fvi.FileVersion }
  if (-not $binVer -and $fvi.ProductVersion) { $binVer = $fvi.ProductVersion }
  if ($binVer) {
    # Normalize to digits and dots (strip possible metadata)
    $norm = ($binVer -replace '[^0-9\.]', '')
    if (-not [string]::IsNullOrWhiteSpace($norm)) { $effectiveVersion = $norm }
  }
} catch { }

Write-Host "Using version $effectiveVersion (project: $projectVersion)"

Write-Host "Compiling installer with Inno Setup..."
# Download WebView2 Evergreen offline installer if not present.
# Default URL is the official Microsoft evergreen offline x64 link. Can be overridden by param or env var WEBVIEW2_URL.
if (-not $WebView2Url -or [string]::IsNullOrWhiteSpace($WebView2Url)) {
  $WebView2Url = $env:WEBVIEW2_URL
}
if (-not $WebView2Url -or [string]::IsNullOrWhiteSpace($WebView2Url)) {
  $WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124701' # Evergreen Standalone x64 (offline)
}

$wv2File = Join-Path (Join-Path $root 'inno') 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
if (-not (Test-Path $wv2File)) {
  Write-Host "Downloading WebView2 runtime from $WebView2Url ..."
  Invoke-WebRequest -Uri $WebView2Url -OutFile $wv2File
}

# Download .NET Desktop Runtime (x64) installer if not present.
# Allow override via parameter or environment DOTNET_DESKTOP_URL; default to Microsoft thank-you link (bootstrapper).
if (-not $DotNetDesktopUrl -or [string]::IsNullOrWhiteSpace($DotNetDesktopUrl)) {
  $DotNetDesktopUrl = $env:DOTNET_DESKTOP_URL
}
if (-not $DotNetDesktopUrl -or [string]::IsNullOrWhiteSpace($DotNetDesktopUrl)) {
  # Default to .NET 8 Desktop Runtime x64 bootstrapper (will fetch latest 8.x during install)
  $DotNetDesktopUrl = 'https://dotnet.microsoft.com/download/dotnet/thank-you/runtime-desktop-8.0.0-windows-x64-installer'
}

$dotnetFile = Join-Path (Join-Path $root 'inno') 'DotNetDesktopRuntimeInstallerX64.exe'
if (-not (Test-Path $dotnetFile)) {
  Write-Host "Downloading .NET Desktop Runtime installer from $DotNetDesktopUrl ..."
  Invoke-WebRequest -Uri $DotNetDesktopUrl -OutFile $dotnetFile
}
# Try common Inno paths
$innoc = ${env:INNOSETUP} ; if (-not $innoc) { $innoc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $innoc)) { $innoc = 'C:\Program Files\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $innoc)) { throw "Inno Setup compiler not found. Install Inno Setup 6 and ensure ISCC.exe is in default path or set INNOSETUP env var to ISCC.exe" }

# Ensure output directory exists and delete existing installer to force overwrite with fresh timestamp
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$targetOut = Join-Path $outDir ("FurchiveSetup-" + $effectiveVersion + ".exe")
$fallbackOut = Join-Path $outDir 'FurchiveSetup.exe'
if (Test-Path $targetOut) {
  Write-Host "Removing existing $targetOut ..."
  Remove-Item -LiteralPath $targetOut -Force -ErrorAction SilentlyContinue
}
if (Test-Path $fallbackOut) {
  Write-Host "Removing existing $fallbackOut ..."
  Remove-Item -LiteralPath $fallbackOut -Force -ErrorAction SilentlyContinue
}

& "$innoc" $iss /Qp "/DAppVersion=$effectiveVersion"

$outfile = $targetOut
if (-not (Test-Path $outfile)) { $outfile = $fallbackOut }
Write-Host "Installer built. See $outfile"
