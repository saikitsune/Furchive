using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Furchive.Avalonia.Messages;
using Avalonia;
using System.Linq;
using Furchive.Avalonia.Infrastructure;

namespace Furchive.Avalonia.Views;

public partial class SettingsWindow : Window
{
    private readonly ISettingsService? _settings;
    private readonly IUnifiedApiService? _api;
    private readonly IThumbnailCacheService? _thumbCache;
    private readonly IPlatformShellService? _shell;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = App.Services?.GetService<ISettingsService>();
        _api = App.Services?.GetService<IUnifiedApiService>();
        _thumbCache = App.Services?.GetService<IThumbnailCacheService>();
        _shell = App.Services?.GetService<IPlatformShellService>();
        LoadValues();
    }

    private void LoadValues()
    {
        var fallback = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
        DownloadDir.Text = _settings?.GetSetting<string>("DefaultDownloadDirectory", fallback) ?? fallback;
        E621User.Text = _settings?.GetSetting<string>("E621Username", "") ?? "";
        E621Key.Text = _settings?.GetSetting<string>("E621ApiKey", "") ?? "";
        FilenameTemplate.Text = _settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}";
        PoolFilenameTemplate.Text = _settings?.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}";
        try { PrefetchPagesAhead.Value = _settings?.GetSetting<int>("E621SearchPrefetchPagesAhead", 2) ?? 2; } catch { PrefetchPagesAhead.Value = 2; }
        try { PrefetchParallelism.Value = _settings?.GetSetting<int>("E621SearchPrefetchParallelism", 2) ?? 2; } catch { PrefetchParallelism.Value = 2; }
        try { ConcurrentDownloads.Value = _settings?.GetSetting<int>("ConcurrentDownloads", 3) ?? 3; } catch { ConcurrentDownloads.Value = 3; }
        DownloadDuplicatesPolicy.Text = _settings?.GetSetting<string>("DownloadDuplicatesPolicy", "skip") ?? "skip";
        try { NetworkTimeoutSeconds.Value = _settings?.GetSetting<int>("NetworkTimeoutSeconds", 30) ?? 30; } catch { NetworkTimeoutSeconds.Value = 30; }
        try { MaxResultsPerSource.Value = _settings?.GetSetting<int>("MaxResultsPerSource", 50) ?? 50; } catch { MaxResultsPerSource.Value = 50; }
        try { CpuWorkerDegree.Value = _settings?.GetSetting<int>("CpuWorkerDegree", Math.Max(1, Environment.ProcessorCount / 2)) ?? Math.Max(1, Environment.ProcessorCount / 2); } catch { CpuWorkerDegree.Value = Math.Max(1, Environment.ProcessorCount / 2); }
        try { PoolsUpdateIntervalMinutes.Value = _settings?.GetSetting<int>("PoolsUpdateIntervalMinutes", 360) ?? 360; } catch { PoolsUpdateIntervalMinutes.Value = 360; }
        try { ThumbnailPrewarmEnabled.IsChecked = _settings?.GetSetting<bool>("ThumbnailPrewarmEnabled", true) ?? true; } catch { ThumbnailPrewarmEnabled.IsChecked = true; }
        try { SaveMetadataJson.IsChecked = _settings?.GetSetting<bool>("SaveMetadataJson", false) ?? false; } catch { SaveMetadataJson.IsChecked = false; }
        try { UseOriginalFilename.IsChecked = _settings?.GetSetting<bool>("UseOriginalFilename", false) ?? false; } catch { UseOriginalFilename.IsChecked = false; }

        // Theme
        try
        {
            var mode = _settings?.GetSetting<string>("ThemeMode", "system") ?? "system";
            ThemeMode.SelectedIndex = mode == "light" ? 1 : mode == "dark" ? 2 : 0;
        }
        catch { ThemeMode.SelectedIndex = 0; }

        // Gallery scale
        try { GalleryScale.Value = _settings?.GetSetting<double>("GalleryScale", 1.0) ?? 1.0; } catch { GalleryScale.Value = 1.0; }
        // Computed UA
        try
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
            var user = _settings?.GetSetting<string>("E621Username", "") ?? "";
            var contact = string.IsNullOrWhiteSpace(user) ? "Anon" : user.Trim();
            ComputedUserAgent.Text = $"Furchive/{version} (by {contact})";
        }
        catch { }

        // Cache and temp info
        RefreshCacheInfo();
        RefreshTempInfo();

        // Pools info (SQLite db)
        try
        {
            PoolsCacheFilePath.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache", "pools_cache.sqlite");
        }
        catch { }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DownloadDir.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621Username", E621User.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621ApiKey", E621Key.Text ?? string.Empty);
            // UA is computed; nothing to save here
            await _settings.SetSettingAsync("FilenameTemplate", FilenameTemplate.Text ?? string.Empty);
            await _settings.SetSettingAsync("PoolFilenameTemplate", PoolFilenameTemplate.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621SearchPrefetchPagesAhead", (int)(PrefetchPagesAhead.Value ?? 2));
            await _settings.SetSettingAsync("E621SearchPrefetchParallelism", (int)(PrefetchParallelism.Value ?? 2));
            // Basic validation
            var policy = (DownloadDuplicatesPolicy.Text ?? "skip").Trim().ToLowerInvariant();
            if (policy != "skip" && policy != "overwrite") policy = "skip";
            await _settings.SetSettingAsync("ConcurrentDownloads", Math.Clamp((int)(ConcurrentDownloads.Value ?? 3), 1, 8));
            await _settings.SetSettingAsync("DownloadDuplicatesPolicy", policy);
            await _settings.SetSettingAsync("SaveMetadataJson", SaveMetadataJson.IsChecked == true);
            await _settings.SetSettingAsync("UseOriginalFilename", UseOriginalFilename.IsChecked == true);
            await _settings.SetSettingAsync("NetworkTimeoutSeconds", Math.Clamp((int)(NetworkTimeoutSeconds.Value ?? 30), 5, 120));
            await _settings.SetSettingAsync("MaxResultsPerSource", Math.Clamp((int)(MaxResultsPerSource.Value ?? 50), 10, 320));
            await _settings.SetSettingAsync("CpuWorkerDegree", Math.Max(1, (int)(CpuWorkerDegree.Value ?? Math.Max(1, Environment.ProcessorCount / 2))));
            await _settings.SetSettingAsync("ThumbnailPrewarmEnabled", ThumbnailPrewarmEnabled.IsChecked == true);
            await _settings.SetSettingAsync("PoolsUpdateIntervalMinutes", Math.Clamp((int)(PoolsUpdateIntervalMinutes.Value ?? 360), 5, 1440));
            // Theme + UI
            var mode = ThemeMode.SelectedIndex == 1 ? "light" : ThemeMode.SelectedIndex == 2 ? "dark" : "system";
            await _settings.SetSettingAsync("ThemeMode", mode);
            await _settings.SetSettingAsync("GalleryScale", GalleryScale.Value);
            // Viewer
            await _settings.SetSettingAsync("VideoAutoplay", VideoAutoplay.IsChecked == true);
            await _settings.SetSettingAsync("VideoStartMuted", VideoStartMuted.IsChecked == true);
            await _settings.SetSettingAsync("ViewerGpuAccelerationEnabled", ViewerGpuAccelerationEnabled.IsChecked == true);
            // Advanced caches
            await _settings.SetSettingAsync("E621MaxPoolDetailConcurrency", Math.Clamp((int)(E621MaxPoolDetailConcurrency.Value ?? 16), 1, 16));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.Search", Math.Clamp((int)(E621SearchTtlMinutes.Value ?? 10), 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.TagSuggest", Math.Clamp((int)(E621TagSuggestTtlMinutes.Value ?? 180), 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.PoolPosts", Math.Clamp((int)(E621PoolPostsTtlMinutes.Value ?? 60), 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.PoolAll", Math.Clamp((int)(E621PoolAllTtlMinutes.Value ?? 360), 1, 1440));
            await _settings.SetSettingAsync("E621CacheTtlMinutes.PostDetails", Math.Clamp((int)(E621PostDetailsTtlMinutes.Value ?? 1440), 1, 1440));
            // Persistent cache
            await _settings.SetSettingAsync("E621PersistentCacheEnabled", E621PersistentCacheEnabled.IsChecked == true);
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.Search", Math.Clamp((int)(E621PersistentCacheMaxEntries_Search.Value ?? 200), 50, 5000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.TagSuggest", Math.Clamp((int)(E621PersistentCacheMaxEntries_TagSuggest.Value ?? 400), 50, 10000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.PoolPosts", Math.Clamp((int)(E621PersistentCacheMaxEntries_PoolPosts.Value ?? 200), 50, 5000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.FullPool", Math.Clamp((int)(E621PersistentCacheMaxEntries_FullPool.Value ?? 150), 50, 5000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.PostDetails", Math.Clamp((int)(E621PersistentCacheMaxEntries_PostDetails.Value ?? 800), 50, 20000));
            await _settings.SetSettingAsync("E621PersistentCacheMaxEntries.PoolDetails", Math.Clamp((int)(E621PersistentCacheMaxEntries_PoolDetails.Value ?? 400), 50, 10000));
            // Notify that settings were saved
            try { WeakReferenceMessenger.Default.Send(new SettingsSavedMessage()); } catch { }
        }
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowseDownloadDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FolderPickerOpenOptions
            {
                AllowMultiple = false
            };
            var current = DownloadDir.Text;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                try
                {
                    var suggested = await StorageProvider.TryGetFolderFromPathAsync(current);
                    if (suggested != null)
                    {
                        options.SuggestedStartLocation = suggested;
                    }
                }
                catch { }
            }
            var result = await StorageProvider.OpenFolderPickerAsync(options);
            if (result != null && result.Count > 0)
            {
                var folder = result[0];
                var path = folder.Path?.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    DownloadDir.Text = path;
                }
            }
        }
        catch { }
    }

    private void OnSoftRefreshPools(object? sender, RoutedEventArgs e)
    {
        try { WeakReferenceMessenger.Default.Send(new PoolsSoftRefreshRequestedMessage(true)); } catch { }
    }

    private void OnRebuildPoolsCache(object? sender, RoutedEventArgs e)
    {
        try { WeakReferenceMessenger.Default.Send(new PoolsCacheRebuildRequestedMessage(true)); } catch { }
    }

    private void OnThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var mode = ThemeMode.SelectedIndex == 1 ? "light" : ThemeMode.SelectedIndex == 2 ? "dark" : "system";
            _ = _settings?.SetSettingAsync("ThemeMode", mode);
        }
        catch { }
    }

    private void OnOpenDownloadsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = DownloadDir.Text;
            if (!string.IsNullOrWhiteSpace(dir)) { _shell?.OpenFolder(dir!); }
        }
        catch { }
    }

    private void OnOpenCacheFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = CachePath.Text;
            if (!string.IsNullOrWhiteSpace(path)) _shell?.OpenFolder(path!);
        }
        catch { }
    }

    private void OnOpenTempFolder(object? sender, RoutedEventArgs e)
    {
        try { var t = TempPath.Text; if (!string.IsNullOrWhiteSpace(t)) _shell?.OpenFolder(t!); } catch { }
    }

    private void OnRefreshCacheInfo(object? sender, RoutedEventArgs e) => RefreshCacheInfo();
    private void OnRefreshTempInfo(object? sender, RoutedEventArgs e) => RefreshTempInfo();

    private void RefreshCacheInfo()
    {
        try
        {
            var path = _thumbCache?.GetCachePath() ?? string.Empty;
            CachePath.Text = path;
            var used = _thumbCache?.GetUsedBytes() ?? 0;
            CacheUsed.Text = BytesToString(used);
        }
        catch { }
    }

    private static string BytesToString(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
        return $"{len:0.##} {sizes[order]}";
    }

    private void RefreshTempInfo()
    {
        try
        {
            var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
            TempPath.Text = tempDir;
            long used = 0;
            try { used = Directory.Exists(tempDir) ? Directory.GetFiles(tempDir).Select(f => new FileInfo(f).Length).Sum() : 0; } catch { }
            TempUsed.Text = BytesToString(used);
        }
        catch { }
    }

    private async void OnClearCache(object? sender, RoutedEventArgs e)
    {
        try { await _thumbCache?.ClearAsync()!; RefreshCacheInfo(); } catch { }
    }

    private void OnClearTemp(object? sender, RoutedEventArgs e)
    {
        try
        {
            var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
            if (!Directory.Exists(tempDir)) return;
            foreach (var f in Directory.GetFiles(tempDir)) { try { File.Delete(f); } catch { } }
            RefreshTempInfo();
        }
        catch { }
    }

    private async void OnAuthenticateE621(object? sender, RoutedEventArgs e)
    {
        try
        {
            var apis = App.Services?.GetServices<IPlatformApi>();
            var e621 = apis?.FirstOrDefault(p => p.PlatformName == "e621");
            if (e621 == null) return;
            var creds = new Dictionary<string, string> { ["UserAgent"] = ComputedUserAgent.Text ?? string.Empty };
            if (!string.IsNullOrWhiteSpace(E621User.Text)) creds["Username"] = E621User.Text!.Trim();
            if (!string.IsNullOrWhiteSpace(E621Key.Text)) creds["ApiKey"] = E621Key.Text!.Trim();
            var ok = await e621.AuthenticateAsync(creds);
            var health = await e621.GetHealthAsync();
            var msg = health.IsAvailable ? ($"e621 {(health.IsAuthenticated ? "authenticated" : "reachable")}. RL remaining: {health.RateLimitRemaining}") : "e621 not reachable";
            await DialogService.ShowInfoAsync(this, ok ? "Authenticated" : "Authentication", msg);
            if (ok && _settings != null)
            {
                // Persist credentials on success
                await _settings.SetSettingAsync("E621Username", E621User.Text?.Trim() ?? string.Empty);
                await _settings.SetSettingAsync("E621ApiKey", E621Key.Text?.Trim() ?? string.Empty);
                // Update computed UA display immediately
                try
                {
                    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                    var contact = string.IsNullOrWhiteSpace(E621User.Text) ? "Anon" : E621User.Text!.Trim();
                    ComputedUserAgent.Text = $"Furchive/{version} (by {contact})";
                }
                catch { }
                // Notify rest of app
                try { WeakReferenceMessenger.Default.Send(new SettingsSavedMessage()); } catch { }
            }
        }
        catch { }
    }

    private void OnRefreshPoolsCacheInfo(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache", "pools_cache.sqlite");
            PoolsCacheFilePath.Text = file;
            if (File.Exists(file))
            {
                // Read meta and pool count from SQLite
                try
                {
                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Cache=Shared");
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT value FROM meta WHERE key='pools_saved_at'";
                        var val = cmd.ExecuteScalar() as string;
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            if (DateTime.TryParse(val, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                                PoolsLastCachedAt.Text = dt.ToLocalTime().ToString("G");
                            else PoolsLastCachedAt.Text = val;
                        }
                        else PoolsLastCachedAt.Text = "(unknown)";
                    }
                    using (var cmd2 = conn.CreateCommand())
                    {
                        cmd2.CommandText = "SELECT COUNT(1) FROM pools";
                        var count = Convert.ToInt32(cmd2.ExecuteScalar());
                        PoolsCachedCount.Text = count.ToString();
                    }
                }
                catch { PoolsLastCachedAt.Text = "(error)"; PoolsCachedCount.Text = "?"; }
            }
            else { PoolsLastCachedAt.Text = "(none)"; PoolsCachedCount.Text = "0"; }
        }
        catch { }
    }

    private void OnClearE621SearchCache(object? sender, RoutedEventArgs e) { try { _api?.ClearE621SearchCache(); } catch { } }
    private void OnClearE621TagSuggestCache(object? sender, RoutedEventArgs e) { try { _api?.ClearE621TagSuggestCache(); } catch { } }
    private void OnClearE621PoolPostsCache(object? sender, RoutedEventArgs e) { try { _api?.ClearE621PoolPostsCache(); } catch { } }
    private void OnClearE621FullPoolCache(object? sender, RoutedEventArgs e) { try { _api?.ClearE621FullPoolCache(); } catch { } }
    private void OnClearE621PostDetailsCache(object? sender, RoutedEventArgs e) { try { _api?.ClearE621PostDetailsCache(); } catch { } }
    private void OnClearE621PoolDetailsCache(object? sender, RoutedEventArgs e) { try { _api?.ClearE621PoolDetailsCache(); } catch { } }
}
