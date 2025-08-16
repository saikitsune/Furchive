# how to build the installer
### Prerequisites
- Inno Setup 6 installed (default path works). If installed elsewhere, set environment variable INNOSETUP to the ISCC.exe path.

### Build from VS Code:
- Run Task: “installer: build-inno”

### Or from terminal:

```
powershell -ExecutionPolicy Bypass -File ".\installer\build-installer.ps1" -Configuration Release -Runtime win-x64 -Version 1.0.0
```

### Output:
- FurchiveSetup.exe

### What the installer does
- Installs Furchive into Program Files (64-bit)
- Creates Start Menu entry and optional Desktop shortcut
- Installs WebView2 runtime silently (/install /silent /norestart) if not already present
- Launches the app at the end (unless running silent)
