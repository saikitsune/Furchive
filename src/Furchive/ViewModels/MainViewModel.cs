using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.Messaging;
using Furchive.Messages;

namespace Furchive.ViewModels;

/// <summary>
/// Main application view model
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IUnifiedApiService _apiService;
    private readonly IDownloadService _downloadService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly string _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache");
    private readonly IPlatformApi? _e621Platform; // keep a handle to re-auth when settings change

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isE621Enabled = true;

    // Removed other platforms (FurAffinity, InkBunny, Weasyl) to focus on e621

    [ObservableProperty]
    private bool _isSearching = false;

    [ObservableProperty]
    private MediaItem? _selectedMedia;

    partial void OnSelectedMediaChanged(MediaItem? value)
    {
        OnPropertyChanged(nameof(IsSelectedDownloaded));
    _ = EnsurePreviewPoolInfoAsync(value);
    }

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private int _ratingFilterIndex = 0; // 0: All, 1: Explicit, 2: Questionable, 3: Safe

    public ObservableCollection<MediaItem> SearchResults { get; } = new();
    public ObservableCollection<string> IncludeTags { get; } = new();
    public ObservableCollection<string> ExcludeTags { get; } = new();
    public ObservableCollection<ContentRating> SelectedRatings { get; } = new() { ContentRating.Safe };
    public ObservableCollection<DownloadJob> DownloadQueue { get; } = new();

    // Pools UI
    [ObservableProperty]
    private string _poolSearch = string.Empty;

    [ObservableProperty]
    private PoolInfo? _selectedPool;

    [ObservableProperty]
    private bool _isPoolMode = false;

    [ObservableProperty]
    private int? _currentPoolId = null;

    // Preview pool context (shown even when not in pool mode if detectable)
    [ObservableProperty]
    private string _previewPoolName = string.Empty;

    public string PreviewPoolDisplayName => !string.IsNullOrWhiteSpace(PreviewPoolName)
        ? PreviewPoolName
        : (SelectedPool?.Name ?? string.Empty);

    public bool PreviewPoolVisible => IsPoolMode || !string.IsNullOrWhiteSpace(PreviewPoolName);

    public ObservableCollection<PoolInfo> Pools { get; } = new();
    public ObservableCollection<PoolInfo> FilteredPools { get; } = new();

    [ObservableProperty]
    private bool _isPoolsLoading = false;

    [ObservableProperty]
    private string _poolsStatusText = string.Empty;

    [ObservableProperty]
    private int _poolsProgressCurrent = 0;

    [ObservableProperty]
    private int _poolsProgressTotal = 0;

    [ObservableProperty]
    private bool _poolsProgressHasTotal = false;

    // Download button label switches in pool mode
    public string DownloadAllLabel => IsPoolMode ? "Download Pool" : "Download All";
    partial void OnIsPoolModeChanged(bool value) => OnPropertyChanged(nameof(DownloadAllLabel));

    // Track pools known to be empty (no visible posts). We'll exclude them from UI once detected.
    private HashSet<int> _excludedPoolIds = new();

    // Saved searches
    public partial class SavedSearch
    {
        public string Name { get; set; } = string.Empty;
        public List<string> IncludeTags { get; set; } = new();
        public List<string> ExcludeTags { get; set; } = new();
        public int RatingFilterIndex { get; set; }
        public string? SearchQuery { get; set; }
    }

    [ObservableProperty]
    private string _saveSearchName = string.Empty;

    public ObservableCollection<SavedSearch> SavedSearches { get; } = new();

    public Dictionary<string, PlatformHealth> PlatformHealth { get; private set; } = new();

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private bool _hasNextPage = false;

    [ObservableProperty]
    private int _totalCount = 0;

    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => HasNextPage;
    public string PageInfo => $"Page {CurrentPage}{(TotalCount > 0 ? $" • {TotalCount} total" : string.Empty)}";

    // Last session persistence
    private sealed class LastSession
    {
        public bool IsPoolMode { get; set; }
        public int? PoolId { get; set; }
        public string? SearchQuery { get; set; }
        public List<string> Include { get; set; } = new();
        public List<string> Exclude { get; set; } = new();
        public int RatingFilterIndex { get; set; }
        public int Page { get; set; } = 1;
    }

    // Gallery sizing: base 180x210 tile with scale factor from settings (0.75 - 1.5)
    public double GalleryTileWidth => 180 * GalleryScale;
    public double GalleryTileHeight => 210 * GalleryScale;
    public double GalleryImageSize => 170 * GalleryScale;
    private double _galleryScale = 1.0;
    public double GalleryScale
    {
        get => _galleryScale;
        set
        {
            if (Math.Abs(_galleryScale - value) > 0.0001)
            {
                _galleryScale = value;
                OnPropertyChanged(nameof(GalleryScale));
                OnPropertyChanged(nameof(GalleryTileWidth));
                OnPropertyChanged(nameof(GalleryTileHeight));
                OnPropertyChanged(nameof(GalleryImageSize));
            }
        }
    }

    public MainViewModel(
        IUnifiedApiService apiService,
        IDownloadService downloadService,
        ISettingsService settingsService,
        ILogger<MainViewModel> logger,
        IEnumerable<IPlatformApi> platformApis)
    {
        _apiService = apiService;
        _downloadService = downloadService;
        _settingsService = settingsService;
        _logger = logger;

        // Register platform APIs
    foreach (var p in platformApis)
        {
            _apiService.RegisterPlatform(p);
            if (string.Equals(p.PlatformName, "e621", StringComparison.OrdinalIgnoreCase))
            {
                _e621Platform = p;
            }
        }

        // Subscribe to download events
        _downloadService.DownloadStatusChanged += OnDownloadStatusChanged;
        _downloadService.DownloadProgressUpdated += OnDownloadProgressUpdated;

        // Load initial settings
        LoadSettings();
    // Initialize gallery scale
    GalleryScale = Math.Clamp(_settingsService.GetSetting<double>("GalleryScale", 1.0), 0.75, 1.5);

    // Compute Favorites visibility
    UpdateFavoritesVisibility();

        // Authenticate platforms from stored settings
        _ = Task.Run(() => AuthenticatePlatformsAsync(platformApis));

        // Check platform health on startup
        _ = Task.Run(CheckPlatformHealthAsync);

        // Load pools cache and schedule incremental updates
        _ = Task.Run(async () =>
        {
            try { await LoadPoolsFromCacheAsync(); } catch { }
            try { await StartOrKickIncrementalAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Pools incremental scheduler failed"); }
        });

    // Try to restore last session shortly after startup (on UI context to avoid cross-thread collection updates)
    _ = RestoreLastSessionAsync();

        // Listen for pools cache rebuilds from Settings
        WeakReferenceMessenger.Default.Register<PoolsCacheRebuiltMessage>(this, async (_, __) =>
        {
            try
            {
                await LoadPoolsFromCacheAsync();
                // No stale check; the sender just rebuilt the cache
                App.Current.Dispatcher.Invoke(() =>
                {
                    ApplyPoolsFilter();
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update pools after cache rebuild notification");
            }
        });

        // Handle requests to rebuild the pools cache from scratch
        WeakReferenceMessenger.Default.Register<PoolsCacheRebuildRequestedMessage>(this, async (_, __) =>
        {
            try
            {
                // Clear in-memory list and delete existing cache file if any
                App.Current.Dispatcher.Invoke(() =>
                {
                    Pools.Clear();
                    FilteredPools.Clear();
                    PoolsStatusText = "rebuilding cache…";
                });
                var file = GetPoolsCacheFilePath();
                try { if (File.Exists(file)) File.Delete(file); } catch { }
                _poolsCacheLastSavedUtc = DateTime.MinValue;
                await RefreshPoolsIfStaleAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to rebuild pools cache on request");
            }
        });

        // Live-apply settings that affect shell UI
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (_, __) =>
        {
            try
            {
                // Update Favorites visibility when username/api key changed
                UpdateFavoritesVisibility();
                // Apply gallery scale live
                GalleryScale = Math.Clamp(_settingsService.GetSetting<double>("GalleryScale", GalleryScale), 0.75, 1.5);
            }
            catch { }
        });

        // Listen for soft refresh requests from Settings window
        WeakReferenceMessenger.Default.Register<PoolsSoftRefreshRequestedMessage>(this, async (_, __) =>
        {
            try { await SoftRefreshPoolsAsync(); } catch { }
        });
    }

    public bool IsSelectedDownloaded
    {
        get
        {
            var item = SelectedMedia;
            if (item == null) return false;
            var defaultDir = _settingsService.GetSetting<string>("DefaultDownloadDirectory",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            // Try to predict final path using filename template
            var hasPoolContext = (item.TagCategories != null && (item.TagCategories.ContainsKey("page_number") || item.TagCategories.ContainsKey("pool_name"))) || IsPoolMode;
            var template = hasPoolContext
                ? (_settingsService.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
                : (_settingsService.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
            string Sanitize(string s)
            {
                var invalid = Path.GetInvalidFileNameChars();
                var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray());
                return clean.Replace(" ", "_");
            }
            var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension;
            var rel = template
                .Replace("{source}", item.Source)
                .Replace("{artist}", Sanitize(item.Artist))
                .Replace("{id}", item.Id)
                .Replace("{safeTitle}", Sanitize(item.Title))
                .Replace("{ext}", ext)
                .Replace("{pool_name}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : (SelectedPool?.Name ?? string.Empty)))
                .Replace("{page_number}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty));
            var fullPath = Path.Combine(defaultDir, rel);
            if (File.Exists(fullPath)) return true;

            // Fallback: if downloaded via pool aggregate without local pool context, check default pool template location
            try
            {
                var poolsRoot = Path.Combine(defaultDir, item.Source, "pools", Sanitize(item.Artist));
                if (Directory.Exists(poolsRoot))
                {
                    // Common default pattern is "{page_number}_{id}.*" under pool folders; search recursively
                    bool match(string file)
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        return name != null && (name.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + item.Id, StringComparison.OrdinalIgnoreCase) || name.Contains(item.Id, StringComparison.OrdinalIgnoreCase));
                    }
                    foreach (var file in Directory.EnumerateFiles(poolsRoot, "*", SearchOption.AllDirectories))
                    {
                        if (match(file)) return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }

    private static string? TryGetExtensionFromUrl(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var uri = new Uri(url);
            var ext = Path.GetExtension(uri.AbsolutePath).Trim('.').ToLowerInvariant();
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch { return null; }
    }

    private async Task AuthenticatePlatformsAsync(IEnumerable<IPlatformApi> platformApis)
    {
    string BuildUa()
    {
        var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        var euserLocal = _settingsService.GetSetting<string>("E621Username", "") ?? "";
    var uname = string.IsNullOrWhiteSpace(euserLocal) ? "Anon" : euserLocal.Trim();
        return $"Furchive/{version} (by {uname})";
    }
    var ua = BuildUa();
    var euser = _settingsService.GetSetting<string>("E621Username", "") ?? "";
    var ekey = _settingsService.GetSetting<string>("E621ApiKey", "")?.Trim() ?? "";

        foreach (var p in platformApis)
        {
            try
            {
                if (p.PlatformName == "e621")
                {
                    var creds = new Dictionary<string, string> { ["UserAgent"] = ua };
                    if (!string.IsNullOrWhiteSpace(euser)) creds["Username"] = euser;
                    if (!string.IsNullOrWhiteSpace(ekey)) creds["ApiKey"] = ekey;
                    await p.AuthenticateAsync(creds);
                }
                // other platforms removed
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auth bootstrap for {Platform} failed", p.PlatformName);
            }
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsSearching) return;

        try
        {
            // Exit pool mode on manual search
            IsPoolMode = false;
            CurrentPoolId = null;
            await PerformSearchAsync(1, reset: true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
            _logger.LogError(ex, "Search failed");
        }
        finally
        {
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanGoNext || IsSearching) return;
        try
        {
            if (IsPoolMode && CurrentPoolId.HasValue)
            {
                await PerformPoolPageAsync(CurrentPoolId.Value, CurrentPage + 1, reset: true);
            }
            else
            {
                await PerformSearchAsync(CurrentPage + 1, reset: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
            _logger.LogError(ex, "Next page failed");
        }
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (!CanGoPrev || IsSearching) return;
        try
        {
            if (IsPoolMode && CurrentPoolId.HasValue)
            {
                await PerformPoolPageAsync(CurrentPoolId.Value, CurrentPage - 1, reset: true);
            }
            else
            {
                await PerformSearchAsync(CurrentPage - 1, reset: true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
            _logger.LogError(ex, "Prev page failed");
        }
    }

    private async Task PerformSearchAsync(int page, bool reset)
    {
        // Always reset IsSearching even if an exception occurs
        try
        {
            if (App.Current.Dispatcher.CheckAccess())
            {
                IsSearching = true;
                StatusMessage = "Searching...";
                if (reset) SearchResults.Clear();
            }
            else
            {
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    IsSearching = true;
                    StatusMessage = "Searching...";
                    if (reset) SearchResults.Clear();
                });
            }

            // Ensure e621 authentication is applied before querying, so auth-only posts have file urls
            await EnsureE621AuthAsync();

            var sources = new List<string>();
            if (IsE621Enabled) sources.Add("e621");
            if (!sources.Any())
            {
                // Fallback to e621 by default
                sources.Add("e621");
            }

            // Build include/exclude tags from explicit lists and search box
            var (inlineInclude, inlineExclude) = ParseQuery(SearchQuery);
            var includeTags = IncludeTags.Union(inlineInclude, StringComparer.OrdinalIgnoreCase).ToList();
            var excludeTags = ExcludeTags.Union(inlineExclude, StringComparer.OrdinalIgnoreCase).ToList();

            // Ratings from UI filter
            var ratings = RatingFilterIndex switch
            {
                0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, // All
                1 => new List<ContentRating> { ContentRating.Explicit },
                2 => new List<ContentRating> { ContentRating.Questionable },
                3 => new List<ContentRating> { ContentRating.Safe },
                _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }
            };

            var searchParams = new SearchParameters
            {
                IncludeTags = includeTags,
                ExcludeTags = excludeTags,
                Sources = sources,
                Ratings = ratings,
                Sort = Furchive.Core.Models.SortOrder.Newest,
                Page = page,
                Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50)
            };

            var result = await _apiService.SearchAsync(searchParams);

            // Add results on UI thread
            if (App.Current.Dispatcher.CheckAccess())
            {
                foreach (var item in result.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl))
                    {
                        item.PreviewUrl = item.FullImageUrl;
                    }
                    SearchResults.Add(item);
                }
                CurrentPage = page;
                HasNextPage = result.HasNextPage;
                TotalCount = result.TotalCount;
                OnPropertyChanged(nameof(CanGoPrev));
                OnPropertyChanged(nameof(CanGoNext));
                OnPropertyChanged(nameof(PageInfo));
            }
            else
            {
                await App.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var item in result.Items)
                    {
                        if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl))
                        {
                            item.PreviewUrl = item.FullImageUrl;
                        }
                        SearchResults.Add(item);
                    }
                    CurrentPage = page;
                    HasNextPage = result.HasNextPage;
                    TotalCount = result.TotalCount;
                    OnPropertyChanged(nameof(CanGoPrev));
                    OnPropertyChanged(nameof(CanGoNext));
                    OnPropertyChanged(nameof(PageInfo));
                });
            }

            var status = result.Errors.Any()
                ? $"Found {result.Items.Count} items with errors: {string.Join(", ", result.Errors.Keys)}"
                : $"Found {result.Items.Count} items";
            if (App.Current.Dispatcher.CheckAccess()) StatusMessage = status;
            else await App.Current.Dispatcher.InvokeAsync(() => StatusMessage = status);
            // Persist last session after a search completes
            try { await PersistLastSessionAsync(); } catch { }
        }
        finally
        {
            if (App.Current.Dispatcher.CheckAccess()) IsSearching = false;
            else await App.Current.Dispatcher.InvokeAsync(() => IsSearching = false);
        }
    }

    // Ensure e621 API is authenticated with current settings (UA, username, api key)
    private async Task EnsureE621AuthAsync()
    {
        try
        {
            if (_e621Platform == null) return;
            string BuildUa()
            {
                var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                var euserLocal = _settingsService.GetSetting<string>("E621Username", "") ?? "";
                var uname = string.IsNullOrWhiteSpace(euserLocal) ? "Anon" : euserLocal.Trim();
                return $"Furchive/{version} (by {uname})";
            }
            var ua = BuildUa();
            var euser = _settingsService.GetSetting<string>("E621Username", "") ?? "";
            var ekey = _settingsService.GetSetting<string>("E621ApiKey", "")?.Trim() ?? "";
            var creds = new Dictionary<string, string> { ["UserAgent"] = ua };
            if (!string.IsNullOrWhiteSpace(euser)) creds["Username"] = euser;
            if (!string.IsNullOrWhiteSpace(ekey)) creds["ApiKey"] = ekey;
            await _e621Platform.AuthenticateAsync(creds);
        }
        catch { }
    }

    private async Task EnsurePreviewPoolInfoAsync(MediaItem? item)
    {
        try
        {
            if (item == null)
            {
                PreviewPoolName = string.Empty;
                OnPropertyChanged(nameof(PreviewPoolDisplayName));
                OnPropertyChanged(nameof(PreviewPoolVisible));
                return;
            }
            // Prefer pool context already present on the item (from search/details enrichment)
            if (item.TagCategories != null &&
                (item.TagCategories.ContainsKey("pool_name") || item.TagCategories.ContainsKey("pool_id")))
            {
                // Set preview name from tag first
                if (item.TagCategories.TryGetValue("pool_name", out var names) && names.Count > 0)
                {
                    PreviewPoolName = names[0];
                }
                else
                {
                    PreviewPoolName = string.Empty;
                }

                // Try to set SelectedPool so the View Pool button enables and ID shows
                try
                {
                    PoolInfo? pool = null;
                    if (item.TagCategories.TryGetValue("pool_id", out var ids) && ids.Count > 0 && int.TryParse(ids[0], out var pid))
                    {
                        pool = Pools.FirstOrDefault(p => p.Id == pid) ?? new PoolInfo { Id = pid, Name = PreviewPoolName ?? string.Empty };
                    }
                    else if (!string.IsNullOrWhiteSpace(PreviewPoolName))
                    {
                        pool = Pools.FirstOrDefault(p => string.Equals(p.Name, PreviewPoolName, StringComparison.OrdinalIgnoreCase))
                            ?? new PoolInfo { Id = 0, Name = PreviewPoolName };
                    }
                    if (pool != null)
                    {
                        SelectedPool = pool;
                    }
                }
                catch { }

                OnPropertyChanged(nameof(PreviewPoolDisplayName));
                OnPropertyChanged(nameof(PreviewPoolVisible));
                return;
            }
            // Otherwise try to fetch pool context from platform (e621)
            if (_e621Platform != null && string.Equals(item.Source, "e621", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var ctx = await _e621Platform.GetPoolContextForPostAsync(item.Id);
                    if (ctx.HasValue)
                    {
                        var (poolId, poolName, pageNumber) = ctx.Value;
                        item.TagCategories ??= new Dictionary<string, List<string>>();
                        item.TagCategories["pool_id"] = new List<string> { poolId.ToString() };
                        item.TagCategories["pool_name"] = new List<string> { poolName };
                        if (pageNumber > 0)
                            item.TagCategories["page_number"] = new List<string> { pageNumber.ToString("D5") };
                        // Also set SelectedPool if it matches cached list, so View Pool is enabled
                        var sp = Pools.FirstOrDefault(p => p.Id == poolId) ?? new PoolInfo { Id = poolId, Name = poolName };
                        if (sp != null) SelectedPool = sp;
                        PreviewPoolName = poolName;
                        OnPropertyChanged(nameof(PreviewPoolDisplayName));
                        OnPropertyChanged(nameof(PreviewPoolVisible));
                        return;
                    }
                }
                catch { }
            }
            PreviewPoolName = string.Empty;
            OnPropertyChanged(nameof(PreviewPoolDisplayName));
            OnPropertyChanged(nameof(PreviewPoolVisible));
        }
        catch { }
    }

    // Pools logic
    partial void OnPoolSearchChanged(string value) { /* no auto-filter; user clicks Filter */ }

    partial void OnSelectedPoolChanged(PoolInfo? value)
    {
        // No auto-load to avoid accidental fetch; user clicks command instead
    }

    private void ApplyPoolsFilter()
    {
        try
        {
            FilteredPools.Clear();
            if (string.IsNullOrWhiteSpace(PoolSearch))
            {
                foreach (var p in Pools.Take(1000)) FilteredPools.Add(p);
                PoolsStatusText = $"{FilteredPools.Count} pools";
                return;
            }
            var q = PoolSearch.Trim();
            bool isNumber = int.TryParse(q, out var id);            
            foreach (var p in Pools)
            {
                if (isNumber)
                {
                    if (p.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)) FilteredPools.Add(p);
                }
                else if (p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredPools.Add(p);
                }
                if (FilteredPools.Count >= 1000) break; // safety cap
            }
            PoolsStatusText = $"{FilteredPools.Count} pools";
        }
        catch { }
    }

    private string GetPoolsCacheFilePath() => Path.Combine(_cacheDir, "e621_pools.json");

    private async Task LoadPoolsFromCacheAsync()
    {
        try
        {
            var file = GetPoolsCacheFilePath();
            if (File.Exists(file))
            {
                var json = await File.ReadAllTextAsync(file);
                var cache = JsonSerializer.Deserialize<PoolsCache>(json) ?? new PoolsCache();
                if (cache.Items != null && cache.Items.Any())
                {
                    App.Current.Dispatcher.Invoke(() =>
                    {
                        Pools.Clear();
                        foreach (var p in cache.Items)
                        {
                            if (!p.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && p.PostCount > 0)
                                Pools.Add(p);
                        }
                        ApplyPoolsFilter();
                        PoolsStatusText = $"{Pools.Count} pools";
                    });
                    _poolsCacheLastSavedUtc = cache.SavedAt == default ? DateTime.UtcNow.AddDays(-7) : cache.SavedAt;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load pools cache");
        }
    }

    private async Task RefreshPoolsIfStaleAsync()
    {
        // Deprecated full-stale refresh path retained only for initial cache build
        try
        {
            var file = GetPoolsCacheFilePath();
            if (File.Exists(file) && Pools.Any())
            {
                // If cache exists, we no longer do a full reload here
                return;
            }

            IsPoolsLoading = true;
            PoolsProgressCurrent = 0;
            PoolsProgressTotal = 0;
            PoolsProgressHasTotal = false;
            PoolsStatusText = "(0) updating…";
            var progress = new Progress<(int current, int? total)>(tuple =>
            {
                PoolsProgressCurrent = tuple.current;
                PoolsProgressHasTotal = tuple.total.HasValue;
                PoolsProgressTotal = tuple.total ?? 0;
                PoolsStatusText = PoolsProgressHasTotal
                    ? $"({PoolsProgressCurrent}/{PoolsProgressTotal}) updating…"
                    : $"({PoolsProgressCurrent}) updating…";
            });
            var list = await _apiService.GetPoolsAsync("e621", progress);
            // Filter out deleted pools by name prefix and pools with zero remaining posts
            list = list.Where(p => !p.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && p.PostCount > 0).ToList();
            App.Current.Dispatcher.Invoke(() =>
            {
                Pools.Clear();
                foreach (var p in list) Pools.Add(p);
                ApplyPoolsFilter();
                PoolsStatusText = $"{Pools.Count} pools";
            });

            Directory.CreateDirectory(_cacheDir);
            var now = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(new PoolsCache { Items = Pools.ToList(), SavedAt = now });
            await File.WriteAllTextAsync(file, json);
            _poolsCacheLastSavedUtc = now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh pools");
        }
        finally
        {
            IsPoolsLoading = false;
            // After initial full refresh, schedule periodic incremental updates
            _ = Task.Run(() => StartOrKickIncrementalAsync());
        }
    }

    // Track last time cache saved to support incremental API query
    private DateTime _poolsCacheLastSavedUtc = DateTime.MinValue;

    private async Task IncrementalUpdatePoolsAsync(TimeSpan interval)
    {
        try
        {
            // If we have never saved, skip incremental and do nothing
            var since = _poolsCacheLastSavedUtc == DateTime.MinValue
                ? DateTime.UtcNow.AddDays(-7)
                : _poolsCacheLastSavedUtc;

            var updates = await _apiService.GetPoolsUpdatedSinceAsync("e621", since);
            if (updates == null || updates.Count == 0) return;

            // Merge into existing in-memory list
            App.Current.Dispatcher.Invoke(() =>
            {
                var map = Pools.ToDictionary(p => p.Id);
                foreach (var u in updates)
                {
                    if (!u.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && u.PostCount > 0)
                    {
                        map[u.Id] = u; // upsert
                    }
                    else
                    {
                        map.Remove(u.Id); // remove deleted/empty
                    }
                }
                Pools.Clear();
                foreach (var p in map.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    Pools.Add(p);
                ApplyPoolsFilter();
                PoolsStatusText = $"{Pools.Count} pools";
            });

            // Persist merged cache
            var file = GetPoolsCacheFilePath();
            Directory.CreateDirectory(_cacheDir);
            var now = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(new PoolsCache { Items = Pools.ToList(), SavedAt = now });
            await File.WriteAllTextAsync(file, json);
            _poolsCacheLastSavedUtc = now;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incremental pool update failed");
        }
        finally
        {
            // Schedule next incremental refresh
            _ = Task.Delay(interval).ContinueWith(async _ =>
            {
                await StartOrKickIncrementalAsync();
            });
        }
    }

    private async Task StartOrKickIncrementalAsync()
    {
        // If cache doesn't exist or in-memory list is empty, do a one-time full cache build
        try
        {
            var file = GetPoolsCacheFilePath();
            if (!File.Exists(file) || !Pools.Any())
            {
                await RefreshPoolsIfStaleAsync();
            }
        }
        catch { }

        // Read interval from settings with sane defaults
        var minutes = Math.Max(5, _settingsService.GetSetting<int>("PoolsUpdateIntervalMinutes", 360));
        await IncrementalUpdatePoolsAsync(TimeSpan.FromMinutes(minutes));
    }

    [RelayCommand]
    private void RunPoolsFilter()
    {
        ApplyPoolsFilter();
    }

    [RelayCommand]
    private async Task LoadSelectedPoolAsync()
    {
        var pool = SelectedPool;
    if (pool == null || IsSearching) return; // guard against concurrent loads
        try
        {
            IsSearching = true;
            StatusMessage = $"Loading pool {pool.Id} ({pool.Name})...";
            SearchResults.Clear();
            CurrentPage = 1;
            await EnsureE621AuthAsync();
            // Pool mode loads ALL posts in pool order; ignore per-page setting
            IsPoolMode = true;
            CurrentPoolId = pool.Id;
            var items = await _apiService.GetAllPoolPostsAsync("e621", pool.Id);
            // If pool has no visible items, exclude it from UI and cache
            if (items == null || items.Count == 0)
            {
                App.Current.Dispatcher.Invoke(() =>
                {
                    _excludedPoolIds.Add(pool.Id);
                    var toRemove = Pools.FirstOrDefault(p => p.Id == pool.Id);
                    if (toRemove != null) Pools.Remove(toRemove);
                    ApplyPoolsFilter();
                    PoolsStatusText = $"{Pools.Count} pools";
                });
                // Persist updated cache without this pool
                try
                {
                    var file = GetPoolsCacheFilePath();
                    Directory.CreateDirectory(_cacheDir);
                    var now = DateTime.UtcNow;
                    var json = JsonSerializer.Serialize(new PoolsCache { Items = Pools.ToList(), SavedAt = now });
                    await File.WriteAllTextAsync(file, json);
                    _poolsCacheLastSavedUtc = now;
                }
                catch { }
                StatusMessage = "Pool appears empty or unavailable.";
                return;
            }
            // Annotate items with pool context for filename templating
            var poolName = pool.Name;
            for (int i = 0; i < items.Count; i++)
            {
                var pageNum = (i + 1).ToString("D5"); // 00001, 00002, ...
                items[i].TagCategories ??= new Dictionary<string, List<string>>();
                items[i].TagCategories["pool_name"] = new List<string> { poolName };
                items[i].TagCategories["page_number"] = new List<string> { pageNum };
            }
            foreach (var item in items)
            {
                // Defensive: ensure preview has something to show in gallery
                if (string.IsNullOrWhiteSpace(item.PreviewUrl))
                {
                    if (!string.IsNullOrWhiteSpace(item.FullImageUrl)) item.PreviewUrl = item.FullImageUrl;
                }
                SearchResults.Add(item);
            }
            HasNextPage = false; // single logical page for full-pool view
            TotalCount = items.Count;
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(PageInfo));
            StatusMessage = $"Loaded pool {pool.Id}: {SearchResults.Count} items";

            // Persist last session after successful pool load
            try { await PersistLastSessionAsync(); } catch { }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load pool: {ex.Message}";
            _logger.LogError(ex, "Pool load failed");
        }
        finally { IsSearching = false; }
    }

    private async Task PerformPoolPageAsync(int poolId, int page, bool reset)
    {
    // With full-pool load, paging is disabled; keep single logical page.
    await Task.CompletedTask;
    }

    // Pools soft refresh: trigger an immediate incremental update
    [RelayCommand]
    private async Task SoftRefreshPoolsAsync()
    {
        try
        {
            var minutes = Math.Max(5, _settingsService.GetSetting<int>("PoolsUpdateIntervalMinutes", 360));
            await IncrementalUpdatePoolsAsync(TimeSpan.FromMinutes(minutes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Soft refresh pools failed");
        }
    }

    private sealed class PoolsCache
    {
        public List<PoolInfo> Items { get; set; } = new();
        public DateTime SavedAt { get; set; }
    }

    private async Task PersistLastSessionAsync()
    {
        try
        {
            var session = new LastSession
            {
                IsPoolMode = IsPoolMode,
                PoolId = CurrentPoolId,
                SearchQuery = SearchQuery,
                Include = IncludeTags.ToList(),
                Exclude = ExcludeTags.ToList(),
                RatingFilterIndex = RatingFilterIndex,
                Page = CurrentPage
            };
            var json = JsonSerializer.Serialize(session);
            await _settingsService.SetSettingAsync("LastSession", json);
        }
        catch { }
    }

    private async Task RestoreLastSessionAsync()
    {
        try
        {
            var json = _settingsService.GetSetting<string>("LastSession", null);
            if (string.IsNullOrWhiteSpace(json)) return;
            var session = JsonSerializer.Deserialize<LastSession>(json);
            if (session == null) return;

            // Apply ratings and tags
            RatingFilterIndex = session.RatingFilterIndex;
            SearchQuery = session.SearchQuery ?? string.Empty;
            IncludeTags.Clear(); foreach (var t in session.Include ?? new()) IncludeTags.Add(t);
            ExcludeTags.Clear(); foreach (var t in session.Exclude ?? new()) ExcludeTags.Add(t);

            if (session.IsPoolMode && session.PoolId.HasValue)
            {
                // Load the pool directly
                CurrentPoolId = session.PoolId;
                IsPoolMode = true;
                SelectedPool = Pools.FirstOrDefault(p => p.Id == session.PoolId.Value);
                // Fire the load without requiring selection
                await EnsureE621AuthAsync();
                var items = await _apiService.GetAllPoolPostsAsync("e621", session.PoolId.Value);
                if (items != null && items.Count > 0)
                {
                    SearchResults.Clear();
                    var poolName = SelectedPool?.Name ?? (items.FirstOrDefault()?.TagCategories?.GetValueOrDefault("pool_name")?.FirstOrDefault() ?? "");
                    for (int i = 0; i < items.Count; i++)
                    {
                        var pageNum = (i + 1).ToString("D5");
                        items[i].TagCategories ??= new Dictionary<string, List<string>>();
                        items[i].TagCategories["pool_name"] = new List<string> { poolName };
                        items[i].TagCategories["page_number"] = new List<string> { pageNum };
                        if (string.IsNullOrWhiteSpace(items[i].PreviewUrl) && !string.IsNullOrWhiteSpace(items[i].FullImageUrl))
                            items[i].PreviewUrl = items[i].FullImageUrl;
                        SearchResults.Add(items[i]);
                    }
                    CurrentPage = 1;
                    HasNextPage = false;
                    TotalCount = items.Count;
                    OnPropertyChanged(nameof(CanGoPrev));
                    OnPropertyChanged(nameof(CanGoNext));
                    OnPropertyChanged(nameof(PageInfo));
                    StatusMessage = $"Restored last pool: {session.PoolId}";
                    return;
                }
            }

            // Otherwise restore a normal search and page
            await PerformSearchAsync(Math.Max(1, session.Page), reset: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore last session");
        }
    }

    public static (IEnumerable<string> include, IEnumerable<string> exclude) ParseQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return (Enumerable.Empty<string>(), Enumerable.Empty<string>());
        var parts = query.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        var include = new List<string>();
        var exclude = new List<string>();
        foreach (var raw in parts)
        {
            var t = raw.Trim();
            if (t.StartsWith("-"))
            {
                t = t.Substring(1);
                if (!string.IsNullOrWhiteSpace(t)) exclude.Add(t);
            }
            else include.Add(t);
        }
        return (include, exclude);
    }

    public async Task<MediaItem?> FetchNextFromApiAsync(bool forward)
    {
        var ratings = RatingFilterIndex switch
        {
            0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit },
            1 => new List<ContentRating> { ContentRating.Explicit },
            2 => new List<ContentRating> { ContentRating.Questionable },
            3 => new List<ContentRating> { ContentRating.Safe },
            _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }
        };
        var (inc, exc) = ParseQuery(SearchQuery);
        var include = IncludeTags.Union(inc, StringComparer.OrdinalIgnoreCase).ToList();
        var exclude = ExcludeTags.Union(exc, StringComparer.OrdinalIgnoreCase).ToList();
        var page = Math.Max(1, CurrentPage + (forward ? 1 : -1));
        var result = await _apiService.SearchAsync(new SearchParameters
        {
            IncludeTags = include,
            ExcludeTags = exclude,
            Sources = new List<string> { "e621" },
            Ratings = ratings,
            Sort = Furchive.Core.Models.SortOrder.Newest,
            Page = page,
            Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50)
        });
        return result.Items.FirstOrDefault();
    }

    [RelayCommand]
    private void AddIncludeTag(string tag)
    {
        if (!string.IsNullOrWhiteSpace(tag) && !IncludeTags.Contains(tag))
        {
            IncludeTags.Add(tag);
        }
    }

    [RelayCommand]
    private void RemoveIncludeTag(string tag)
    {
        IncludeTags.Remove(tag);
    }

    [RelayCommand]
    private void AddExcludeTag(string tag)
    {
        if (!string.IsNullOrWhiteSpace(tag) && !ExcludeTags.Contains(tag))
        {
            ExcludeTags.Add(tag);
        }
    }

    [RelayCommand]
    private void RemoveExcludeTag(string tag)
    {
        ExcludeTags.Remove(tag);
    }

    [RelayCommand]
    private async Task DownloadSelectedAsync()
    {
        if (SelectedMedia == null) return;

        try
        {
            var downloadPath = _settingsService.GetSetting<string>("DefaultDownloadDirectory", 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            // If in temp, move instead of queueing a new download
            var tempPath = GetTempPathFor(SelectedMedia);
            var finalPath = GenerateFinalPath(SelectedMedia, downloadPath);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            if (File.Exists(tempPath) && !File.Exists(finalPath))
            {
                File.Move(tempPath, finalPath);
                StatusMessage = $"Saved from temp: {SelectedMedia.Title}";
                OnPropertyChanged(nameof(IsSelectedDownloaded));
                return;
            }
            await _downloadService.QueueDownloadAsync(SelectedMedia, downloadPath);
            StatusMessage = $"Queued download: {SelectedMedia.Title}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Download failed: {ex.Message}";
            _logger.LogError(ex, "Download failed for {Title}", SelectedMedia.Title);
        }
    }

    [RelayCommand]
    private async Task DownloadAllAsync()
    {
        if (!SearchResults.Any()) return;

        try
        {
            var downloadPath = _settingsService.GetSetting<string>("DefaultDownloadDirectory", 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            var toQueue = new List<MediaItem>();
            foreach (var m in SearchResults)
            {
                var tempPath = GetTempPathFor(m);
                var finalPath = GenerateFinalPath(m, downloadPath);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (File.Exists(tempPath) && !File.Exists(finalPath))
                {
                    try { File.Move(tempPath, finalPath); }
                    catch { toQueue.Add(m); }
                }
                else
                {
                    toQueue.Add(m);
                }
            }
            if (toQueue.Any())
            {
                if (IsPoolMode && CurrentPoolId.HasValue)
                {
                    // Queue as an aggregate pool download job
                    var label = SelectedPool?.Name ?? PreviewPoolName ?? "Pool";
                    await _downloadService.QueueAggregateDownloadsAsync("pool", toQueue, downloadPath, label);
                }
                else
                {
                    await _downloadService.QueueMultipleDownloadsAsync(toQueue, downloadPath);
                }
            }
            StatusMessage = IsPoolMode ? $"Queued pool downloads ({SearchResults.Count} items)" : $"Queued {SearchResults.Count} downloads";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Batch download failed: {ex.Message}";
            _logger.LogError(ex, "Batch download failed");
        }
    }

    private static string GetTempPathFor(MediaItem item)
    {
        var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
        var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension;
        var safeArtist = new string((item.Artist ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Replace(" ", "_");
        var safeTitle = new string((item.Title ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Replace(" ", "_");
        var file = $"{item.Source}_{item.Id}_{safeArtist}_{safeTitle}.{ext}";
        return Path.Combine(tempDir, file);
    }

    private string GenerateFinalPath(MediaItem mediaItem, string basePath)
    {
        var hasPoolContext = mediaItem.TagCategories != null && (mediaItem.TagCategories.ContainsKey("page_number") || mediaItem.TagCategories.ContainsKey("pool_name"));
        var template = hasPoolContext
            ? (_settingsService.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
            : (_settingsService.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
        var extFinal = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? TryGetExtensionFromUrl(mediaItem.FullImageUrl) ?? "bin" : mediaItem.FileExtension;
        string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray());
            return clean.Replace(" ", "_");
        }
        var filenameRel = template
            .Replace("{source}", mediaItem.Source)
            .Replace("{artist}", Sanitize(mediaItem.Artist))
            .Replace("{id}", mediaItem.Id)
            .Replace("{safeTitle}", Sanitize(mediaItem.Title))
            .Replace("{ext}", extFinal)
            .Replace("{pool_name}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : (SelectedPool?.Name ?? string.Empty)))
            .Replace("{page_number}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty));
        return Path.Combine(basePath, filenameRel);
    }

    [RelayCommand]
    private async Task RefreshDownloadQueueAsync()
    {
        try
        {
            var jobs = await _downloadService.GetDownloadJobsAsync();
            // Hide child jobs in UI when they belong to an aggregate
            jobs = jobs.Where(j => string.IsNullOrEmpty(j.ParentId)).ToList();
            var map = DownloadQueue.ToDictionary(j => j.Id);
            var incomingIds = new HashSet<string>(jobs.Select(j => j.Id));
            // Update existing or add new
            foreach (var job in jobs)
            {
                if (map.TryGetValue(job.Id, out var existing))
                {
                    existing.Status = job.Status;
                    existing.DestinationPath = job.DestinationPath;
                    existing.CompletedAt = job.CompletedAt;
                    existing.ErrorMessage = job.ErrorMessage;
                    existing.TotalBytes = job.TotalBytes;
                    existing.BytesDownloaded = job.BytesDownloaded;
                }
                else
                {
                    DownloadQueue.Add(job);
                }
            }
            // Remove jobs that disappeared
            for (int i = DownloadQueue.Count - 1; i >= 0; i--)
            {
                if (!incomingIds.Contains(DownloadQueue[i].Id))
                    DownloadQueue.RemoveAt(i);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh download queue");
        }
    }

    private async Task CheckPlatformHealthAsync()
    {
        try
        {
            PlatformHealth = await _apiService.GetAllPlatformHealthAsync();
            OnPropertyChanged(nameof(PlatformHealth));
            
            // Update UI enabled states based on health
            IsE621Enabled = IsE621Enabled && PlatformHealth.GetValueOrDefault("e621")?.IsAvailable == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check platform health");
        }
    }

    private void OnDownloadStatusChanged(object? sender, DownloadJob job)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(job.ParentId))
            {
                // Hide child jobs from UI list
                return;
            }
            var existing = DownloadQueue.FirstOrDefault(j => j.Id == job.Id);
            if (existing != null)
            {
                existing.Status = job.Status;
                existing.DestinationPath = job.DestinationPath;
                existing.CompletedAt = job.CompletedAt;
                existing.ErrorMessage = job.ErrorMessage;
                existing.TotalBytes = job.TotalBytes;
                existing.BytesDownloaded = job.BytesDownloaded;
            }
            else
            {
                DownloadQueue.Add(job);
            }

            if (job.Status == DownloadStatus.Completed && SelectedMedia?.Id == job.MediaItem.Id)
            {
                OnPropertyChanged(nameof(IsSelectedDownloaded));
            }
        });
    }

    private void OnDownloadProgressUpdated(object? sender, DownloadJob job)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(job.ParentId)) return; // hide child jobs
            var existing = DownloadQueue.FirstOrDefault(j => j.Id == job.Id);
            if (existing != null)
            {
                existing.BytesDownloaded = job.BytesDownloaded;
                existing.TotalBytes = job.TotalBytes;
            }
        });
    }

    private void LoadSettings()
    {
        var defaultRatings = _settingsService.GetSetting<string>("RatingsDefault", "safe") ?? "safe";
        SelectedRatings.Clear();
        
        if (defaultRatings.Contains("safe")) SelectedRatings.Add(ContentRating.Safe);
        if (defaultRatings.Contains("questionable")) SelectedRatings.Add(ContentRating.Questionable);
        if (defaultRatings.Contains("explicit")) SelectedRatings.Add(ContentRating.Explicit);

        // Load saved searches
        try
        {
            var json = _settingsService.GetSetting<string>("SavedSearches", "[]") ?? "[]";
            var list = JsonSerializer.Deserialize<List<SavedSearch>>(json) ?? new();
            SavedSearches.Clear();
            foreach (var s in list) SavedSearches.Add(s);
        }
        catch { }

        // Ensure Favorites visibility follows settings
        UpdateFavoritesVisibility();
    }

    // Favorites support
    [ObservableProperty]
    private bool _showFavoritesButton;

    private void UpdateFavoritesVisibility()
    {
        var user = _settingsService.GetSetting<string>("E621Username", "") ?? "";
        var key = _settingsService.GetSetting<string>("E621ApiKey", "") ?? "";
        ShowFavoritesButton = !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(key);
    }

    [RelayCommand]
    private async Task FavoritesAsync()
    {
        var user = _settingsService.GetSetting<string>("E621Username", "") ?? "";
        if (string.IsNullOrWhiteSpace(user)) return;
        // Set search to fav:USERNAME and run
        SearchQuery = $"fav:{user}";
        IncludeTags.Clear();
        ExcludeTags.Clear();
        await SearchAsync();
    }

    [RelayCommand]
    private async Task SaveSearchAsync()
    {
        var name = SaveSearchName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var ss = new SavedSearch
        {
            Name = name,
            IncludeTags = IncludeTags.ToList(),
            ExcludeTags = ExcludeTags.ToList(),
            RatingFilterIndex = RatingFilterIndex,
            SearchQuery = SearchQuery
        };

        var existing = SavedSearches.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) SavedSearches.Remove(existing);
        SavedSearches.Add(ss);

        await PersistSavedSearchesAsync();
        SaveSearchName = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteSavedSearchAsync(SavedSearch? ss)
    {
        if (ss == null) return;
        SavedSearches.Remove(ss);
        await PersistSavedSearchesAsync();
    }

    [RelayCommand]
    private async Task ApplySavedSearchAsync(SavedSearch? ss)
    {
        if (ss == null) return;
        IncludeTags.Clear(); foreach (var t in ss.IncludeTags) IncludeTags.Add(t);
        ExcludeTags.Clear(); foreach (var t in ss.ExcludeTags) ExcludeTags.Add(t);
        RatingFilterIndex = ss.RatingFilterIndex;
        SearchQuery = ss.SearchQuery ?? string.Empty;
        await SearchAsync();
    }

    private async Task PersistSavedSearchesAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(SavedSearches.ToList());
            await _settingsService.SetSettingAsync("SavedSearches", json);
        }
        catch { }
    }
}
