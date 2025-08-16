using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.IO;
using System;

namespace Furchive.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThumbnailCacheService _thumbCache;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty] private string? _e621UserAgent;
    [ObservableProperty] private string? _e621Username;
    [ObservableProperty] private string? _e621ApiKey;
    // Removed other platforms (FurAffinity, InkBunny, Weasyl)

    [ObservableProperty] private long _cacheUsedBytes;
    [ObservableProperty] private string _cachePath = string.Empty;
    [ObservableProperty] private string _defaultDownloadDirectory = string.Empty;
    [ObservableProperty] private string _filenameTemplate = string.Empty;
    [ObservableProperty] private bool _videoAutoplay = true;
    [ObservableProperty] private bool _videoStartMuted = false;
    [ObservableProperty] private long _tempUsedBytes;
    [ObservableProperty] private string _tempPath = string.Empty;

    public SettingsViewModel(ISettingsService settings, IThumbnailCacheService thumbCache, ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _thumbCache = thumbCache;
        _logger = logger;

    // Load stored values with dynamic UA default: Furchive/{version} (by {USERNAME})
    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
    var defaultUa = $"Furchive/{version} (by USERNAME)";
    E621UserAgent = _settings.GetSetting<string>("E621UserAgent", defaultUa);
        E621Username = _settings.GetSetting<string>("E621Username", null);
    E621ApiKey = _settings.GetSetting<string>("E621ApiKey", null);

    RefreshCacheInfo();
    RefreshTempInfo();

    // Downloads
    var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads", "Furchive");
    DefaultDownloadDirectory = _settings.GetSetting<string>("DefaultDownloadDirectory", fallback) ?? fallback;
    FilenameTemplate = _settings.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}_{safeTitle}.{ext}") ?? "{source}/{artist}/{id}_{safeTitle}.{ext}";

    // Playback
    VideoAutoplay = _settings.GetSetting<bool>("VideoAutoplay", true);
    VideoStartMuted = _settings.GetSetting<bool>("VideoStartMuted", false);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _settings.SetSettingAsync("E621UserAgent", E621UserAgent ?? string.Empty);
            await _settings.SetSettingAsync("E621Username", E621Username ?? string.Empty);
            // Trim whitespace/spaces from API key before saving
            var apiKeyClean = (E621ApiKey ?? string.Empty).Replace(" ", string.Empty).Trim();
            await _settings.SetSettingAsync("E621ApiKey", apiKeyClean);
            // Other platforms removed; nothing to persist for them

            // Downloads
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DefaultDownloadDirectory ?? string.Empty);
            await _settings.SetSettingAsync("FilenameTemplate", FilenameTemplate ?? string.Empty);

            // Playback
            await _settings.SetSettingAsync("VideoAutoplay", VideoAutoplay);
            await _settings.SetSettingAsync("VideoStartMuted", VideoStartMuted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            throw;
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _thumbCache.ClearAsync();
        RefreshCacheInfo();
    }

    [RelayCommand]
    private void RefreshCacheInfo()
    {
        CachePath = _thumbCache.GetCachePath();
        CacheUsedBytes = _thumbCache.GetUsedBytes();
    }

    [RelayCommand]
    private void RefreshTempInfo()
    {
        TempPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
        try
        {
            if (!Directory.Exists(TempPath))
            {
                TempUsedBytes = 0;
                return;
            }
            long total = 0;
            foreach (var f in Directory.EnumerateFiles(TempPath, "*", SearchOption.AllDirectories))
            {
                try { var fi = new FileInfo(f); total += fi.Length; } catch { }
            }
            TempUsedBytes = total;
        }
        catch { TempUsedBytes = 0; }
    }

    [RelayCommand]
    private Task ClearTempAsync()
    {
        try
        {
            if (Directory.Exists(TempPath))
            {
                foreach (var f in Directory.EnumerateFiles(TempPath, "*", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }
        finally
        {
            RefreshTempInfo();
        }
        return Task.CompletedTask;
    }

    // Validation helpers
    public bool IsE621Valid(out string message)
    {
        if (string.IsNullOrWhiteSpace(E621UserAgent)) { message = "User-Agent is required for e621 requests."; return false; }
        message = "Looks good."; return true;
    }

    // Validation for other platforms removed
}
