![](/assets/icon256.png)
# Furchive - Furry Art Gallery Browser

Furchive is a fast, modern gallery viewer, search, and download application for e621. Built with C# and WPF, it provides an intuitive interface for browsing, previewing, and downloading content from e621.

## Features

- **Multi-Platform Support**: Browse and download from e621, FurAffinity, InkBunny, and Weasyl
- **Unified Search**: Search across multiple platforms simultaneously
- **Advanced Tag Management**: Include/exclude tags with autocomplete
- **Download Queue**: Batch downloads with progress tracking, pause/resume functionality
- **Content Rating Filters**: Filter content by Safe, Questionable, and Explicit ratings
- **e621 Support**: Browse and download directly from e621
- **Powerful Search**: Tag includes/excludes with rating filters
- **Configurable Settings**: Customizable download paths, filename templates, and behavior
- **Modern UI**: Clean, responsive WPF interface using ModernWPF

## Requirements

- .NET 8.0 or later
- Windows 10/11
- Visual Studio 2022 (for development)

## Installation

### From Source

1. Clone the repository:
```bash
git clone https://github.com/yourusername/Furchive.git
cd Furchive
```

2. Restore dependencies:
```powershell
dotnet restore
```

3. Build the solution:
```powershell
dotnet build
```

4. Run the application:
```powershell
dotnet run --project src/Furchive
```

## Configuration

### First Run Setup

1. Set your e621 User-Agent string in Settings (required by e621)

### Settings

- **Download Directory**: Default location for downloaded content
- **Filename Template**: Customize how files are named using variables like `{artist}`, `{title}`, `{id}`
- **Concurrent Downloads**: Number of simultaneous downloads (1-8)
- **Duplicate Policy**: Skip, overwrite, or rename duplicate files
- **Content Ratings**: Default rating filters for searches

## Usage

### Basic Search

1. Enter search terms or use the tag editor (leave empty to fetch the most recent posts)
2. Apply rating filters as needed
3. Click "Search" or press Enter

### Tag Management

- Use the tag editor to add include/exclude tags
- Green tags are included in search
- Red tags are excluded from search
- Autocomplete suggestions available when typing

### Downloads

- Click "Download" for individual items
- Use "Download All" for batch downloads
- Monitor progress in the download queue
- Pause, resume, or cancel downloads as needed

### Keyboard Shortcuts

- **Enter**: Execute search (in search box or tag editor)
- **Esc**: Clear current selection

## API Documentation References

- [e621 API](https://e621.wiki)

## Architecture

### Core Components

- **Furchive.Core**: Business logic, models, and platform APIs
- **Furchive**: WPF application with MVVM pattern
- **Furchive.Tests**: Unit and integration tests

### Key Services

- **UnifiedApiService**: Aggregates multiple platform APIs
- **DownloadService**: Manages download queue and file operations
- **SettingsService**: Handles configuration persistence

### Platform APIs

- **E621Api**: e621.net integration

## Development

### Building

```powershell
# Debug build
dotnet build

# Release build
dotnet build -c Release
```

## Installer (Windows)

This repo includes a WiX v4-based installer that:
- Publishes a self-contained x64 build of the app (no .NET runtime required)
- Packages it into an MSI
- Provides a bootstrapper EXE that installs Microsoft Edge WebView2 Runtime if missing, then your MSI

Build steps:
1. Ensure WiX Toolset v4 is installed (via `dotnet tool install --global wix` or WiX build tools in PATH).
2. Build MSI, then Bootstrapper.

Artifacts:
- `installer/msi/bin/Release/Furchive.msi`
- `installer/bundle/bin/Release/FurchiveSetup.exe`

### Running Tests

```powershell
dotnet test
```

### Project Structure

```
Furchive/
├── src/
│   ├── Furchive/              # WPF Application
│   │   ├── Views/             # XAML views
│   │   ├── ViewModels/        # View models
│   │   └── Converters/        # Value converters
│   └── Furchive.Core/         # Core library
│       ├── Models/            # Data models
│       ├── Services/          # Business services
│       ├── Interfaces/        # Service contracts
│       └── Platforms/         # Platform API implementations
└── tests/
    └── Furchive.Tests/        # Unit tests
```

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Disclaimer

This application is not affiliated with e621. Please respect the terms of service and rate limits when using this application.

## Support

For issues, feature requests, or questions, please open an issue on GitHub.
