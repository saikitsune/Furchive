dotnet restore
dotnet build -c Release
dotnet test
dotnet run --project src/Furchive.Avalonia
# Furchive – Quick Start

Concise setup & dev reference. For full details see `README.md`.

## 🚀 Clone & Run

```powershell
git clone https://github.com/saikitsune/Furchive.git
cd Furchive
dotnet restore
dotnet build -c Release
dotnet run --project src/Furchive.Avalonia
```

Run tests:
```powershell
dotnet test
```

Publish (multi‑RID helper):
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-avalonia.ps1 -Configuration Release
```

## � First Run
1. Open Settings → set e621 User‑Agent (and optional username + API key).
2. Choose download directory & tweak filename templates if desired.
3. (Optional) Enable Last Session Restore.

## 🧭 Core Concepts
- Search: include / exclude tags + rating filter.
- Pools: cached in SQLite; background incremental refresh; can pin favorites.
- Downloads: queued with progress, pause / resume; filenames generated from templates.
- Saved Searches: name current include/exclude + rating + page index.

## 🛠 Common Scripts
| Purpose | Command |
| ------- | ------- |
| Windows installer | `installer/build-installer.ps1 -Configuration Release -Runtime win-x64` |
| macOS DMG | `scripts/package-macos-dmg.sh osx-arm64` |
| Linux AppImage | `scripts/package-linux-appimage.sh linux-x64` |

## 🧩 Extend A Platform
1. Implement `IPlatformApi` (map to `MediaItem`).
2. Register in DI (`App.axaml.cs`).
3. Add authentication properties to settings if required.

## � Structure
```
src/
	Furchive.Avalonia/   UI (Avalonia, MVVM)
	Furchive.Core/       Logic & services
	Furchive/            Legacy WPF (reference)
tests/Furchive.Tests/  Unit tests
installer/             Inno Setup build script
docs/                  Specs (JSON)
```

## 🧪 Focus of Tests
Pure logic: tag parsing, path templating, session persistence, download queue state transitions.

## 🔮 Roadmap (High Value)
- Enhanced viewer: zoom/pan, better video pipeline
- Rich tag autocomplete & categorization visuals
- Advanced keyboard shortcuts & accessibility
- Additional platforms behind feature flags

## 🆘 Help
Open a GitHub issue with repro steps + logs (if applicable).

Happy hacking.
