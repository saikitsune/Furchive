param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$Version = "1.0.5",
  # Optional: override the download URL for the WebView2 runtime (e.g., to a GitHub Release asset)
  [string]$WebView2Url
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
$appProj = Join-Path $repo 'src/Furchive/Furchive.csproj'
$publishDir = Join-Path $repo 'src/Furchive/publish'
$iss = Join-Path $root 'inno/Furchive.iss'

Write-Host "Publishing app ($Configuration, $Runtime) to $publishDir..."
& dotnet publish $appProj -c $Configuration -r $Runtime --self-contained true -p:PublishTrimmed=false -p:Version=$Version -o $publishDir

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
# Try common Inno paths
$innoc = ${env:INNOSETUP} ; if (-not $innoc) { $innoc = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $innoc)) { $innoc = 'C:\Program Files\Inno Setup 6\ISCC.exe' }
if (-not (Test-Path $innoc)) { throw "Inno Setup compiler not found. Install Inno Setup 6 and ensure ISCC.exe is in default path or set INNOSETUP env var to ISCC.exe" }

& "$innoc" $iss /Qp

Write-Host "Installer built. See installer/inno/output/FurchiveSetup.exe"
