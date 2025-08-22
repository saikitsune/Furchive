# Furchive – AI Assistant Project Guide

Concise, actionable rules so an AI agent can contribute productively immediately. Keep answers terse, implement directly, and follow existing patterns.

## 1. Architecture Snapshot
- Solution split: `Furchive.Core` (models + services + platform APIs), `Furchive.Avalonia` (UI), legacy WPF project (ignore), and `Furchive.Tests`.
- Strict MVVM: All UI logic resides in ViewModels (e.g. `MainViewModel.cs`). XAML code-behind only for simple event wiring (key handlers, window sizing, opening dialogs/windows).
- Dependency Injection via `Microsoft.Extensions.Hosting` configured in `App.axaml.cs` (services.AddSingleton / AddTransient). Prefer injecting interfaces from `Furchive.Core.Interfaces`.
- Platform abstraction: `IPlatformApi` (currently e621) registered dynamically; `UnifiedApiService` orchestrates multi‑platform searches & caching.
- Caching/persistence: SQLite-backed stores (`SqlitePoolsCacheStore`, `E621SqliteCacheStore`) plus settings JSON via `SettingsService` under `%LOCALAPPDATA%/Furchive`.
- Downloads: `DownloadService` raises events consumed in `MainViewModel` for UI updates. Filenames resolved with configurable templates.

## 2. Key Patterns & Conventions
- Use `CommunityToolkit.Mvvm` attributes: `[ObservableProperty]`, `[RelayCommand]`. Never hand-write boilerplate properties unless needed.
- Threading: UI mutations must happen on `Dispatcher.UIThread`. Pattern: `if (Dispatcher.UIThread.CheckAccess()) { ... } else await Dispatcher.UIThread.InvokeAsync(() => { ... });`
- Error handling: swallow non-critical failures (log if logger available) to keep UI resilient. Send user-facing errors with `WeakReferenceMessenger` + `UiErrorMessage`.
- Pool loading: Attempt cached posts first, then background refresh; maintain responsiveness.
- Tag parsing: Helper `ParseQuery` merges inline and explicit include/exclude tags; multi-tag additions split on whitespace.
- Settings persistence: After any state change that should survive restarts (page, tags, ratings, pool state) call `PersistLastSessionAsync` if `LoadLastSessionEnabled`.
- File naming: Use templates with placeholders `{source}`, `{artist}`, `{id}`, `{pool_name}`, `{page_number}`, etc. Always sanitize path segments (see `GenerateFinalPath`).

## 3. UI Implementation Notes
- XAML lives in `Views/`. Bind only to ViewModel public properties/commands. Avoid adding logic in `.axaml.cs` beyond: input event adaptation, layout persistence (splitter sizes), launching auxiliary windows (`SettingsWindow`).
- Add new commands in ViewModel via `[RelayCommand]`; expose enable state via boolean properties + `OnSelectedMediaChanged` to notify.
- For new dialogs or windows: construct inside code-behind, not in ViewModel (keep UI layer dependent on services, not vice versa).

## 4. Adding Features – Checklist Example
When adding (e.g.) a new “Open Containing Folder” button for selected media:
1. In `MainViewModel`: add prop `CanOpenSelectedFile` + `[RelayCommand] OpenSelectedFile()` using `_shell.OpenFolder(Path.GetDirectoryName(finalPath))`.
2. Raise `OnPropertyChanged` for capability in `OnSelectedMediaChanged`.
3. Bind button in `MainWindow.axaml` with `Command` + `IsEnabled` bindings (no code-behind unless custom event args needed).
4. Build (Release) using existing VS Code task `build-after-edits` and fix compile errors.
5. (Optional) Add unit test in `Furchive.Tests` for path generation logic only (UI excluded).

## 5. Performance & Responsiveness
- Never block UI: wrap long operations in `Task.Run` then marshal results to UI thread.
- Use existing throttling patterns (see prefetch & incremental update logic) when adding batch network calls.
- For lists: always Clear()+Add range instead of replacing ObservableCollection object to keep bindings stable.

## 6. Data Flow Highlights
- Search: UI -> `SearchCommand` -> `PerformSearchAsync` -> `UnifiedApiService.SearchAsync` -> results appended to `SearchResults`.
- Pool selection: ListBox selection -> handler -> `LoadSelectedPoolAsync` (cached + background refresh) -> `SearchResults`.
- Downloads: Queue commands -> `DownloadService` -> events -> ViewModel listeners update `DownloadQueue` rows.

## 7. External Integration
- e621 API: Auth requires User-Agent (+ optional username + API key). UA built dynamically each request to reflect saved settings.
- Shell operations: via `IPlatformShellService` (`OpenUrl`, `OpenFolder`, `OpenPath`); never call `Process.Start` directly in UI code.
- WebView (video/HTML) gated by `HAS_WEBVIEW_AVALONIA` constant in project file; feature-code should respect `WebViewEnabled` setting.

## 8. Testing Guidance
- Target pure logic: tag parsing (`ParseQuery`), filename path resolution, session persistence boundaries (mock settings service).
- Avoid UI thread dependency in tests—abstract time & file system where practical.

## 9. Common Pitfalls (Avoid)
- Writing synchronous blocking I/O on UI thread (freeze risk).
- Adding logic into code-behind that belongs in ViewModel (breaks MVVM discipline).
- Forgetting to raise property changed for derived enable-state properties -> buttons stay disabled.
- Creating new collections instead of mutating existing ObservableCollections (breaks binding).

## 10. Build & Packaging
- Primary app: `src/Furchive.Avalonia` (TargetFramework net8.0). Use tasks: `build-avalonia` or `build-after-edits`.
- Release publish (multi-RID): run `scripts/publish-avalonia.ps1` (already used by tasks & installer script).
- Windows installer: `installer/build-installer.ps1` producing Inno Setup EXE; includes WebView2 & runtime fallback.

## 11. When Unsure
Prefer mimicking existing patterns in `MainViewModel`. If adding cross-layer functionality, create an interface in `Furchive.Core.Interfaces`, implement service in Core, register in `App.axaml.cs`, then inject into ViewModel.

Keep responses lean: propose concrete file edits, then apply & build. Ask for clarification only when a blocking ambiguity exists.
