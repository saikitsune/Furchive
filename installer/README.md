# how to build the installer
### Prerequisites
- Inno Setup 6 installed (default path works). If installed elsewhere, set environment variable INNOSETUP to the ISCC.exe path.

### Build from VS Code:

### Or from terminal:

```
powershell -ExecutionPolicy Bypass -File ".\installer\build-installer.ps1" -Configuration Release -Runtime win-x64 -Version 1.0.0
```

### Output:
# Installer

This folder contains packaging scripts for building the Windows installer.

## Inno Setup

- `inno/Furchive.iss` is the installer script.
- `build-installer.ps1` publishes the app self-contained (bundles .NET) and compiles the installer. It downloads the WebView2 runtime if missing. You can override the WebView2 download source with `-WebView2Url` or `WEBVIEW2_URL` environment variable (e.g., to a GitHub Release asset).

Usage:

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 1.0.0
```

Optionally specify a custom WebView2 URL:

```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Configuration Release -Runtime win-x64 -Version 1.0.0 -WebView2Url "https://github.com/<owner>/<repo>/releases/download/v1.0.0/MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
```

The installer executable is output to `installer/inno/output/FurchiveSetup.exe`.

## GitHub Releases workflow

A GitHub Actions workflow `.github/workflows/release.yml` builds the app and the installer when you push a tag like `v1.2.3`, and then creates a release uploading:

- `FurchiveSetup.exe` (the installer)
- `MicrosoftEdgeWebView2RuntimeInstallerX64.exe` (the offline WebView2 runtime installer)

To trigger a release:

```
git tag v1.0.0
git push origin v1.0.0
```
### What the installer does
- Installs Furchive into Program Files (64-bit)
- Creates Start Menu entry and optional Desktop shortcut
- Installs WebView2 runtime silently (/install /silent /norestart) if not already present
- Launches the app at the end (unless running silent)

### What the installer does NOT do
- It does not install the .NET Desktop Runtime. The app is published self-contained, so .NET is bundled with the app binaries.
