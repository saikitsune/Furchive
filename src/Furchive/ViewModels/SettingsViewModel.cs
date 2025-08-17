using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.IO;
using System;
using CommunityToolkit.Mvvm.Messaging;
using Furchive.Messages;

namespace Furchive.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThumbnailCacheService _thumbCache;
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IUnifiedApiService _api;

    [ObservableProperty] private string? _e621UserAgent;
    [ObservableProperty] private string? _e621Username;
    [ObservableProperty] private string? _e621ApiKey;
    // Removed other platforms (FurAffinity, InkBunny, Weasyl)

    [ObservableProperty] private long _cacheUsedBytes;
    [ObservableProperty] private string _cachePath = string.Empty;
    [ObservableProperty] private string _defaultDownloadDirectory = string.Empty;
    [ObservableProperty] private string _filenameTemplate = string.Empty;
    [ObservableProperty] private string _poolFilenameTemplate = string.Empty;
    [ObservableProperty] private bool _videoAutoplay = true;
    [ObservableProperty] private bool _videoStartMuted = false;
    [ObservableProperty] private long _tempUsedBytes;
    [ObservableProperty] private string _tempPath = string.Empty;

    [ObservableProperty] private int _poolsCachedCount;
    [ObservableProperty] private DateTime? _poolsLastCachedAt;
    [ObservableProperty] private string _poolsCacheFilePath = string.Empty;
    [ObservableProperty] private int _poolsUpdateIntervalMinutes;

    public SettingsViewModel(ISettingsService settings, IThumbnailCacheService thumbCache, ILogger<SettingsViewModel> logger, IUnifiedApiService api)
    {
        _settings = settings;
        _thumbCache = thumbCache;
        _logger = logger;
        _api = api;

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
    PoolFilenameTemplate = _settings.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}";

    // Playback
    VideoAutoplay = _settings.GetSetting<bool>("VideoAutoplay", true);
    VideoStartMuted = _settings.GetSetting<bool>("VideoStartMuted", false);
        // Pools cache info
        RefreshPoolsCacheInfo();

    // Pools incremental update interval (minutes)
    var defaultInterval = 360; // 6 hours
    PoolsUpdateIntervalMinutes = Math.Max(5, _settings.GetSetting<int>("PoolsUpdateIntervalMinutes", defaultInterval));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _settings.SetSettingAsync("E621UserAgent", E621UserAgent ?? string.Empty);
            await _settings.SetSettingAsync("E621Username", E621Username ?? string.Empty);
            // Trim leading/trailing whitespace, but do NOT remove internal spaces
            var apiKeyClean = (E621ApiKey ?? string.Empty).Trim();
            await _settings.SetSettingAsync("E621ApiKey", apiKeyClean);
            // Other platforms removed; nothing to persist for them

            // Downloads
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DefaultDownloadDirectory ?? string.Empty);
            await _settings.SetSettingAsync("FilenameTemplate", FilenameTemplate ?? string.Empty);
            await _settings.SetSettingAsync("PoolFilenameTemplate", PoolFilenameTemplate ?? string.Empty);

            // Playback
            await _settings.SetSettingAsync("VideoAutoplay", VideoAutoplay);
            await _settings.SetSettingAsync("VideoStartMuted", VideoStartMuted);

            // Pools update interval
            var interval = PoolsUpdateIntervalMinutes <= 0 ? 360 : PoolsUpdateIntervalMinutes;
            await _settings.SetSettingAsync("PoolsUpdateIntervalMinutes", interval);
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

    private static string GetPoolsCachePath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache");
        return Path.Combine(dir, "e621_pools.json");
    }

    [RelayCommand]
    private void RefreshPoolsCacheInfo()
    {
        try
        {
            PoolsCacheFilePath = GetPoolsCachePath();
            PoolsCachedCount = 0;
            PoolsLastCachedAt = null;
            if (File.Exists(PoolsCacheFilePath))
            {
                using var fs = File.OpenRead(PoolsCacheFilePath);
                using var doc = System.Text.Json.JsonDocument.Parse(fs);
                if (doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    PoolsCachedCount = items.GetArrayLength();
                }
                if (doc.RootElement.TryGetProperty("SavedAt", out var saved))
                {
                    if (saved.ValueKind == System.Text.Json.JsonValueKind.String && DateTime.TryParse(saved.GetString(), out var dt))
                        PoolsLastCachedAt = dt;
                }
            }
        }
        catch { /* ignore parse errors */ }
    }

    [RelayCommand]
    private Task RebuildPoolsCacheAsync()
    {
        try
        {
            var path = GetPoolsCachePath();
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            // Delete old cache JSON so the app treats it as missing
            try { if (File.Exists(path)) File.Delete(path); } catch { }

            // Notify the app shell to rebuild from scratch (MainViewModel will do the full fetch)
            WeakReferenceMessenger.Default.Send(new PoolsCacheRebuildRequestedMessage());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to request pools cache rebuild");
        }
        finally
        {
            RefreshPoolsCacheInfo();
        }
        return Task.CompletedTask;
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
