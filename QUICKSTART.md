# Furchive - Quick Start Guide

## 🎯 Overview
Furchive is a cross‑platform C# Avalonia application for browsing and downloading furry art from e621. The UI runs on Windows, macOS, and Linux, powered by a shared Core library.

## ✅ What's Been Implemented

### 🏗️ Architecture
- **Clean Architecture**: Separation between Core (business logic) and UI layers
- **MVVM Pattern**: ViewModels handle UI logic, Views are pure presentation
- **Dependency Injection**: Microsoft.Extensions.DI with proper service lifetimes
- **Modern Avalonia**: Fluent theme with responsive layouts

### 🔧 Core Services
- **UnifiedApiService**: Aggregates platform APIs (currently e621)
- **DownloadService**: Concurrent download management with progress tracking
- **SettingsService**: JSON-based configuration with automatic persistence
- **Platform APIs**: Stub implementations ready for e621, FA, InkBunny, Weasyl

### 🎨 UI Components
- **MainWindow**: Primary interface with toolbar, gallery grid, and preview pane
- **Tag Management**: Include/exclude tags with visual feedback
- **Download Queue**: Real-time progress tracking with pause/resume/cancel
- **Status Bar**: Platform health indicators and operation feedback

## 🚀 Quick Start Commands

```powershell
# Navigate to project directory
cd "C:\projects\github\Furchive"

# Restore packages
dotnet restore

# Build the solution (WPF project is excluded from default builds)
dotnet build -c Release

# Run tests
dotnet test

# Launch the Avalonia application (Windows)
dotnet run --project src/Furchive.Avalonia

# Publish cross‑platform
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-avalonia.ps1 -Configuration Release
```

## 📋 Development Checklist

### ✅ Completed
- [x] Solution structure with 4 projects (Core, Avalonia, WPF-legacy, Tests)
- [x] Core models (MediaItem, SearchParameters, DownloadJob, etc.)
- [x] Service interfaces and implementations
- [x] MVVM ViewModels with CommunityToolkit
- [x] Avalonia XAML with Fluent styling
- [x] Settings management with JSON persistence
- [x] Basic unit tests
- [x] Comprehensive error handling
- [x] Complete API specification (JSON format)

### 🔄 Next Steps (Future Implementation)
- [ ] Enhance viewer window (video support, zoom, pan)
- [ ] Tag autocomplete with suggestions
- [ ] Keyboard shortcuts and accessibility
- [ ] macOS/Linux packaging beyond raw publish

## 🔍 Key Features

### Search
- Tag management with include/exclude logic
- Content rating filters (Safe/Questionable/Explicit)

### Download Management
- Concurrent downloads with progress tracking
- Configurable filename templates
- Duplicate handling policies (skip/overwrite/rename)
- Metadata export to JSON

### User Experience
- Modern Avalonia interface with dark/light theme support
- Gallery grid with thumbnail previews
- Detailed preview pane with metadata
- Real-time download queue management

## 📁 Project Structure

```
Furchive/
├── src/
│   ├── Furchive.Avalonia/     # Avalonia UI (primary app)
│   ├── Furchive/              # Legacy WPF app (kept for reference; excluded from default builds)
│   └── Furchive.Core/         # Business Logic
├── tests/
│   └── Furchive.Tests/        # Unit Tests
├── docs/
│   └── furchive-specifications.json  # Complete API/UI/Settings spec
└── README.md
```

## ⚙️ Configuration

Settings are automatically persisted to:
- **Windows**: `%LOCALAPPDATA%\Furchive\settings.json`

Key settings include:
- Download directory and filename templates
- e621 authentication (User-Agent, optional username/API key)
- Content rating preferences
- Download behavior (concurrency, duplicates, etc.)

## 🐛 Known Issues
- None blocking. Please report issues on GitHub.

## 🎨 UI Framework
Built with:
- **.NET 8**
- **Avalonia 11** for cross‑platform UI
- **CommunityToolkit.Mvvm** for MVVM helpers

---

The application is now ready for development and testing. The core architecture is solid and extensible, making it straightforward to implement the remaining platform-specific features.
