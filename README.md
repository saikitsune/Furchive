![](/assets/icon256.png)
# Furchive

Cross‑platform (Windows / macOS / Linux) Avalonia application for browsing, searching, and downloading furry artwork from e621. Fast incremental pool caching, resilient downloads, and flexible filename templating backed by a clean Core + UI separation.

## ✨ Highlights

- Unified search with include / exclude tags & rating filters (Safe / Questionable / Explicit)
- Incremental pools browser (SQLite cache + background refresh & pruning)
- Download queue (concurrent, pause / resume, progress + ETA, persistence across runs)
- Filename templates with placeholders (e.g. `{source}`, `{artist}`, `{id}`, `{pool_name}`, `{page_number}`, `{ext}`) + automatic sanitization
- Saved searches & last‑session restoration (optional)
- Virtual tag helpers (e.g. `no_artist` → internal filter)
- Pinned pools & quick filtering
- Dark Fluent UI, responsive gallery scaling, lazy page append while viewing
- Cross‑platform packaging scripts + Windows installer (Inno Setup)

## 🛠 Architecture Overview

- Strict MVVM (CommunityToolkit.Mvvm attributes for properties & commands)
- Dependency Injection via `Microsoft.Extensions.Hosting`
- `IPlatformApi` abstraction (currently e621 registered; extensible for others)
- `UnifiedApiService` dispatches searches to registered platforms & aggregates results
- Pools caching via `SqlitePoolsCacheStore`; incremental updates + pruning service
- Settings persisted to `%LOCALAPPDATA%/Furchive` (JSON)
- Download pipeline (`DownloadService`) raises events consumed by `MainViewModel`
- UI thread safety enforced with `Dispatcher.UIThread`

## 🔐 e621 Etiquette

Set a proper User‑Agent in Settings: it is required by e621. Optional username + API key (for higher rate limits / auth). Furchive dynamically rebuilds the UA string with the current version.

## 🚀 Install & Run

### Windows (Installer)
Download the latest `FurchiveSetup-<version>.exe` from Releases. The installer:
1. Publishes a self‑contained x64 build (no .NET install needed)
2. Ensures Microsoft Edge WebView2 Runtime (bundled offline installer)
3. Installs per user: `%LOCALAPPDATA%\Programs\Furchive`

### From Source (All Platforms)

```powershell
git clone https://github.com/saikitsune/Furchive.git
cd Furchive
dotnet restore
dotnet build -c Release
dotnet run --project src/Furchive.Avalonia
```

### Publishing (CLI)

```powershell
# Windows self-contained publish
dotnet publish src/Furchive.Avalonia/Furchive.Avalonia.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64

# macOS (arm64 example)
dotnet publish src/Furchive.Avalonia/Furchive.Avalonia.csproj -c Release -r osx-arm64 -p:PublishSingleFile=true -o publish/osx-arm64

# Linux (x64)
dotnet publish src/Furchive.Avalonia/Furchive.Avalonia.csproj -c Release -r linux-x64 -p:PublishSingleFile=true -o publish/linux-x64
```

Packaging helper scripts:
- `scripts/package-macos-dmg.sh <rid>`
- `scripts/package-linux-appimage.sh <rid>`
- `installer/build-installer.ps1` (Windows Inno Setup EXE)

## ⚙️ Key Settings

| Setting | Purpose |
| ------- | ------- |
| User-Agent / Username / API Key | e621 identification/auth |
| DefaultDownloadDirectory | Root for saved media |
| FilenameTemplate / PoolFilenameTemplate | Per‑item path & name structure |
| GalleryScale | UI gallery tile scaling |
| PoolsUpdateIntervalMinutes | Incremental refresh cadence |
| LoadLastSessionEnabled | Restore search state & page |
| MaxResultsPerSource | Per‑page fetch size |

Templates support: `{source}`, `{artist}`, `{id}`, `{safeTitle}`, `{pool_name}`, `{page_number}`, `{ext}`.

## 🔍 Using Furchive

1. Enter include tags; prefix excluded tags in the exclude panel (UI chips differentiate color).
2. Choose rating filter (all / explicit / questionable / safe).
3. Search. Scroll to trigger lazy append or open viewer to auto-fetch more quietly.
4. Switch to Pool Mode by selecting a pool; page navigation respects pool context.
5. Pin frequently used pools for quick access.
6. Queue downloads (single, selected, or entire pool). Monitor progress & ETA in the Downloads panel.

Pool Cache Lifecycle:
- First run: full fetch stored in SQLite.
- Subsequent runs: incremental delta updates (background) at the configured interval.
- Manual refresh: Settings → Rebuild / Soft Refresh options.

## 🧩 Extending Platforms

1. Implement `IPlatformApi` in Core (new folder under `Platforms/`).
2. Register in DI (see `App.axaml.cs`).
3. Ensure authentication flow populates credentials; update UnifiedApiService registration.
4. Add tagging / rating mapping to match internal `MediaItem` model.

## 🛠 Development Notes

- Avoid blocking the UI thread (wrap I/O heavy tasks with `Task.Run` then marshal back via `Dispatcher.UIThread`).
- Never replace `ObservableCollection` instances; mutate them to retain bindings.
- Use `[ObservableProperty]` & `[RelayCommand]` attributes instead of manual boilerplate.
- On state changes that should persist (page, tags, ratings) call session persistence if enabled.


## 🛑 Disclaimer

Not affiliated with e621. Respect their terms, rate limits, and content policies. Always set a meaningful User‑Agent string.

## 💬 Support / Issues

Use GitHub Issues for bugs, feature requests, and questions. Please include steps to reproduce and any relevant logs.

---
See `QUICKSTART.md` for a condensed onboarding reference.
