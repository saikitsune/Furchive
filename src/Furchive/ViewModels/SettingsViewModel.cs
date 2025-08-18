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

    // No longer user-editable UA; compute from version + username
    [ObservableProperty] private string? _e621Username;
    [ObservableProperty] private string? _e621ApiKey;
    public string ComputedUserAgent
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
            var user = string.IsNullOrWhiteSpace(E621Username) ? "Anon" : E621Username!.Trim();
            return $"Furchive/{version} (by {user})";
        }
    }

    partial void OnE621UsernameChanged(string? value)
    {
        OnPropertyChanged(nameof(ComputedUserAgent));
    }
    // Removed other platforms (FurAffinity, InkBunny, Weasyl)

    [ObservableProperty] private long _cacheUsedBytes;
    [ObservableProperty] private string _cachePath = string.Empty;
    [ObservableProperty] private string _defaultDownloadDirectory = string.Empty;
    [ObservableProperty] private string _filenameTemplate = string.Empty;
    [ObservableProperty] private string _poolFilenameTemplate = string.Empty;
    [ObservableProperty] private bool _videoAutoplay = true;
    [ObservableProperty] private bool _videoStartMuted = false;
    // Viewer rendering options
    [ObservableProperty] private bool _viewerGpuAccelerationEnabled = true;
    [ObservableProperty] private bool _viewerLazyDecodeEnabled = true;
    [ObservableProperty] private long _tempUsedBytes;
    [ObservableProperty] private string _tempPath = string.Empty;
    [ObservableProperty] private int _concurrentDownloads;
    [ObservableProperty] private double _galleryScale; // 1.0 default
    [ObservableProperty] private int _postsPerPage;

    [ObservableProperty] private int _poolsCachedCount;
    [ObservableProperty] private DateTime? _poolsLastCachedAt;
    [ObservableProperty] private string _poolsCacheFilePath = string.Empty;
    [ObservableProperty] private int _poolsUpdateIntervalMinutes;

    // Search Cache (e621) – advanced tuning
    [ObservableProperty] private int _e621MaxPoolDetailConcurrency; // 1-16
    [ObservableProperty] private int _e621SearchTtlMinutes;         // 1-1440
    [ObservableProperty] private int _e621TagSuggestTtlMinutes;     // 1-1440
    [ObservableProperty] private int _e621PoolPostsTtlMinutes;      // 1-1440
    [ObservableProperty] private int _e621PoolAllTtlMinutes;        // 1-1440
    [ObservableProperty] private int _e621PostDetailsTtlMinutes;    // 1-1440
    [ObservableProperty] private int _e621PoolsTtlMinutes;          // 1-1440 (pool metadata)
    // Search prefetching
    [ObservableProperty] private int _e621SearchPrefetchPagesAhead; // 0-5
    [ObservableProperty] private int _e621SearchPrefetchParallelism; // 1-4

    // Persistent cache toggle and caps
    [ObservableProperty] private bool _e621PersistentCacheEnabled;
    [ObservableProperty] private int _e621PersistentCacheMaxSearchEntries;
    [ObservableProperty] private int _e621PersistentCacheMaxTagSuggestEntries;
    [ObservableProperty] private int _e621PersistentCacheMaxPoolPostsEntries;
    [ObservableProperty] private int _e621PersistentCacheMaxFullPoolEntries;
    [ObservableProperty] private int _e621PersistentCacheMaxPostDetailsEntries;
    [ObservableProperty] private int _e621PersistentCacheMaxPoolDetailsEntries;

    public SettingsViewModel(ISettingsService settings, IThumbnailCacheService thumbCache, ILogger<SettingsViewModel> logger, IUnifiedApiService api)
    {
        _settings = settings;
        _thumbCache = thumbCache;
        _logger = logger;
        _api = api;

    // Load stored values with dynamic UA default: Furchive/{version} (by {USERNAME})
    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        E621Username = _settings.GetSetting<string>("E621Username", null);
    E621ApiKey = _settings.GetSetting<string>("E621ApiKey", null);

    RefreshCacheInfo();
    RefreshTempInfo();

    // Downloads
    var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
    DefaultDownloadDirectory = _settings.GetSetting<string>("DefaultDownloadDirectory", fallback) ?? fallback;
    FilenameTemplate = _settings.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}";
    PoolFilenameTemplate = _settings.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}";
    ConcurrentDownloads = Math.Clamp(_settings.GetSetting<int>("ConcurrentDownloads", 3), 1, 4);
    GalleryScale = Math.Clamp(_settings.GetSetting<double>("GalleryScale", 1.0), 0.75, 1.5);
    PostsPerPage = Math.Clamp(_settings.GetSetting<int>("MaxResultsPerSource", 50), 1, 100);

    // Playback
    VideoAutoplay = _settings.GetSetting<bool>("VideoAutoplay", true);
    VideoStartMuted = _settings.GetSetting<bool>("VideoStartMuted", false);
    ViewerGpuAccelerationEnabled = _settings.GetSetting<bool>("ViewerGpuAccelerationEnabled", true);
    ViewerLazyDecodeEnabled = _settings.GetSetting<bool>("ViewerLazyDecodeEnabled", true);
        // Pools cache info
        RefreshPoolsCacheInfo();

    // Pools incremental update interval (minutes)
    var defaultInterval = 360; // 6 hours
    PoolsUpdateIntervalMinutes = Math.Max(5, _settings.GetSetting<int>("PoolsUpdateIntervalMinutes", defaultInterval));

    // Search Cache defaults (take effect on next app start)
    E621MaxPoolDetailConcurrency = Math.Clamp(_settings.GetSetting<int>("E621MaxPoolDetailConcurrency", 16), 1, 16);
    // Recommended defaults: Search 10, TagSuggest 180, PoolPosts 60, FullPool 360, PostDetails 1440, Pools 360
    E621SearchTtlMinutes = Math.Clamp(_settings.GetSetting<int>("E621CacheTtlMinutes.Search", 10), 1, 1440);
    E621TagSuggestTtlMinutes = Math.Clamp(_settings.GetSetting<int>("E621CacheTtlMinutes.TagSuggest", 180), 1, 1440);
    E621PoolPostsTtlMinutes = Math.Clamp(_settings.GetSetting<int>("E621CacheTtlMinutes.PoolPosts", 60), 1, 1440);
    E621PoolAllTtlMinutes = Math.Clamp(_settings.GetSetting<int>("E621CacheTtlMinutes.PoolAll", 360), 1, 1440);
    E621PostDetailsTtlMinutes = Math.Clamp(_settings.GetSetting<int>("E621CacheTtlMinutes.PostDetails", 1440), 1, 1440);
    E621PoolsTtlMinutes = Math.Clamp(_settings.GetSetting<int>("E621CacheTtlMinutes.Pools", 360), 1, 1440);
    E621SearchPrefetchPagesAhead = Math.Clamp(_settings.GetSetting<int>("E621SearchPrefetchPagesAhead", 2), 0, 5);
    E621SearchPrefetchParallelism = Math.Clamp(_settings.GetSetting<int>("E621SearchPrefetchParallelism", 2), 1, 4);

    // Persistent cache defaults
    E621PersistentCacheEnabled = _settings.GetSetting<bool>("E621PersistentCacheEnabled", false);
    E621PersistentCacheMaxSearchEntries = Math.Clamp(_settings.GetSetting<int>("E621PersistentCacheMaxEntries.Search", 200), 50, 5000);
    E621PersistentCacheMaxTagSuggestEntries = Math.Clamp(_settings.GetSetting<int>("E621PersistentCacheMaxEntries.TagSuggest", 400), 50, 10000);
    E621PersistentCacheMaxPoolPostsEntries = Math.Clamp(_settings.GetSetting<int>("E621PersistentCacheMaxEntries.PoolPosts", 200), 50, 5000);
    E621PersistentCacheMaxFullPoolEntries = Math.Clamp(_settings.GetSetting<int>("E621PersistentCacheMaxEntries.FullPool", 150), 50, 5000);
    E621PersistentCacheMaxPostDetailsEntries = Math.Clamp(_settings.GetSetting<int>("E621PersistentCacheMaxEntries.PostDetails", 800), 50, 20000);
    E621PersistentCacheMaxPoolDetailsEntries = Math.Clamp(_settings.GetSetting<int>("E621PersistentCacheMaxEntries.PoolDetails", 400), 50, 10000);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            // User-Agent is computed automatically; do not persist
            await _settings.SetSettingAsync("E621Username", E621Username ?? string.Empty);
            // Trim leading/trailing whitespace, but do NOT remove internal spaces
            var apiKeyClean = (E621ApiKey ?? string.Empty).Trim();
            await _settings.SetSettingAsync("E621ApiKey", apiKeyClean);
            // Other platforms removed; nothing to persist for them

            // Downloads
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DefaultDownloadDirectory ?? string.Empty);
            await _settings.SetSettingAsync("FilenameTemplate", FilenameTemplate ?? string.Empty);
            await _settings.SetSettingAsync("PoolFilenameTemplate", PoolFilenameTemplate ?? string.Empty);
            await _settings.SetSettingAsync("ConcurrentDownloads", Math.Clamp(ConcurrentDownloads, 1, 4));
            await _settings.SetSettingAsync("GalleryScale", Math.Clamp(GalleryScale, 0.75, 1.5));
            await _settings.SetSettingAsync("MaxResultsPerSource", Math.Clamp(PostsPerPage, 1, 100));

            // Playback
            await _settings.SetSettingAsync("VideoAutoplay", VideoAutoplay);
            await _settings.SetSettingAsync("VideoStartMuted", VideoStartMuted);
            await _settings.SetSettingAsync("ViewerGpuAccelerationEnabled", ViewerGpuAccelerationEnabled);
            await _settings.SetSettingAsync("ViewerLazyDecodeEnabled", ViewerLazyDecodeEnabled);

            // Pools update interval
            var interval = PoolsUpdateIntervalMinutes <= 0 ? 360 : PoolsUpdateIntervalMinutes;
            await _settings.SetSettingAsync("PoolsUpdateIntervalMinutes", interval);

            // Search Cache (advanced)
            await _settings.SetSettingAsync("E621MaxPoolDetailConcurrency", Math.Clamp(E621MaxPoolDetailConcurrency, 1, 16));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.Search", Math.Clamp(E621SearchTtlMinutes, 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.TagSuggest", Math.Clamp(E621TagSuggestTtlMinutes, 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.PoolPosts", Math.Clamp(E621PoolPostsTtlMinutes, 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.PoolAll", Math.Clamp(E621PoolAllTtlMinutes, 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.PostDetails", Math.Clamp(E621PostDetailsTtlMinutes, 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.Pools", Math.Clamp(E621PoolsTtlMinutes, 1, 1440));
            await _settings.SetSettingAsync("E621SearchPrefetchPagesAhead", Math.Clamp(E621SearchPrefetchPagesAhead, 0, 5));
            await _settings.SetSettingAsync("E621SearchPrefetchParallelism", Math.Clamp(E621SearchPrefetchParallelism, 1, 4));

            // Persistent cache
            await _settings.SetSettingAsync("E621PersistentCacheEnabled", E621PersistentCacheEnabled);
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.Search", Math.Clamp(E621PersistentCacheMaxSearchEntries, 50, 5000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.TagSuggest", Math.Clamp(E621PersistentCacheMaxTagSuggestEntries, 50, 10000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.PoolPosts", Math.Clamp(E621PersistentCacheMaxPoolPostsEntries, 50, 5000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.FullPool", Math.Clamp(E621PersistentCacheMaxFullPoolEntries, 50, 5000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.PostDetails", Math.Clamp(E621PersistentCacheMaxPostDetailsEntries, 50, 20000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.PoolDetails", Math.Clamp(E621PersistentCacheMaxPoolDetailsEntries, 50, 10000));

            // Notify the rest of the app that settings have changed so UI can live-refresh
            WeakReferenceMessenger.Default.Send(new SettingsSavedMessage());
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
        // UA is always computed; just ensure we have the computed value
        if (string.IsNullOrWhiteSpace(ComputedUserAgent)) { message = "Failed to compute User-Agent."; return false; }
        message = "Looks good."; return true;
    }

    // Validation for other platforms removed
}
