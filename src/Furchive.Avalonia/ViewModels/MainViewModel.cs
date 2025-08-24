using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Controls.ApplicationLifetimes;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Furchive.Avalonia.Messages;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Text.Json;
// (imports trimmed)

namespace Furchive.Avalonia.ViewModels;

// Port of the WPF MainViewModel adapted for Avalonia (Dispatcher.UIThread instead of App.Current.Dispatcher)
public partial class MainViewModel : ObservableObject
{
    private readonly IUnifiedApiService _apiService;
    private readonly IDownloadService _downloadService;
    private readonly ISettingsService _settingsService;
    private readonly IThumbnailCacheService? _thumbCache;
    private readonly ICpuWorkQueue? _cpuQueue;
    private readonly ILogger<MainViewModel> _logger;
    private readonly string _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache");
    private readonly IPoolsCacheStore _cacheStore;
    private readonly IPlatformApi? _e621Platform;
    private readonly IPlatformShellService? _shell;
    private readonly IPoolPruningService _pruningService;

    [ObservableProperty] private bool _isE621Enabled = true;
    [ObservableProperty] private bool _isSearching = false;
    [ObservableProperty] private MediaItem? _selectedMedia;
    partial void OnSelectedMediaChanged(MediaItem? value) { OnPropertyChanged(nameof(IsSelectedDownloaded)); OnPropertyChanged(nameof(CanOpenSelectedInBrowser)); OnPropertyChanged(nameof(CanOpenViewer)); _ = EnsurePreviewPoolInfoAsync(value); }
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _isBackgroundCaching = false;
    [ObservableProperty] private int _backgroundCachingCurrent = 0;
    [ObservableProperty] private int _backgroundCachingTotal = 0;
    public int BackgroundCachingPercent => BackgroundCachingTotal <= 0 ? 0 : (int)Math.Round(100.0 * BackgroundCachingCurrent / (double)BackgroundCachingTotal);
    [ObservableProperty] private int _backgroundCachingItemsFetched = 0;
    [ObservableProperty] private int _ratingFilterIndex = 0;
    partial void OnRatingFilterIndexChanged(int value) { try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) _ = PersistLastSessionAsync(); } catch { } }

    public ObservableCollection<MediaItem> SearchResults { get; } = new();
    public ObservableCollection<string> IncludeTags { get; } = new();
    public ObservableCollection<string> ExcludeTags { get; } = new();
    public ObservableCollection<ContentRating> SelectedRatings { get; } = new() { ContentRating.Safe };
    public ObservableCollection<DownloadJob> DownloadQueue { get; } = new();

    // Aggregated tag categories across all current SearchResults so include/exclude tag chips can be color coded
    // independent of the currently selected media item. Key = category name (artist/character/etc.),
    // Value = list of tags belonging to that category (deduplicated). We expose as a materialized dictionary of lists
    // for compatibility with existing TagStringToCategoryBrushConverter which expects Dictionary<string,List<string>>.
    private readonly Dictionary<string, HashSet<string>> _aggregatedTagSets = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> AggregatedTagCategories => _aggregatedTagSets.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
    private void AddItemTagCategories(MediaItem item)
    {
        try
        {
            if (item?.TagCategories == null) return;
            foreach (var kv in item.TagCategories)
            {
                if (kv.Value == null) continue;
                if (!_aggregatedTagSets.TryGetValue(kv.Key, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _aggregatedTagSets[kv.Key] = set;
                }
                foreach (var tag in kv.Value)
                {
                    if (!string.IsNullOrWhiteSpace(tag)) set.Add(tag);
                }
            }
        }
        catch { }
    }
    private void ClearAggregatedTagCategories()
    {
        _aggregatedTagSets.Clear();
        OnPropertyChanged(nameof(AggregatedTagCategories));
    }

    [ObservableProperty] private string _poolSearch = string.Empty;
    [ObservableProperty] private PoolInfo? _selectedPool;
    partial void OnSelectedPoolChanged(PoolInfo? value) { OnPropertyChanged(nameof(ShowPinPoolButton)); }
    [ObservableProperty] private bool _isPoolMode = false;
    partial void OnIsPoolModeChanged(bool value) { OnPropertyChanged(nameof(DownloadAllLabel)); OnPropertyChanged(nameof(ShowPinPoolButton)); OnPropertyChanged(nameof(CanOpenPoolPage)); try { OpenPoolPageCommand?.NotifyCanExecuteChanged(); } catch { } }
    [ObservableProperty] private int? _currentPoolId = null;
    partial void OnCurrentPoolIdChanged(int? value) { OnPropertyChanged(nameof(CanOpenPoolPage)); try { OpenPoolPageCommand?.NotifyCanExecuteChanged(); } catch { } }
    [ObservableProperty] private string _previewPoolName = string.Empty;
    public string PreviewPoolDisplayName => !string.IsNullOrWhiteSpace(PreviewPoolName) ? PreviewPoolName : (SelectedPool?.Name ?? string.Empty);
    public bool PreviewPoolVisible => IsPoolMode || !string.IsNullOrWhiteSpace(PreviewPoolName);
    public ObservableCollection<PoolInfo> Pools { get; } = new();
    public ObservableCollection<PoolInfo> FilteredPools { get; } = new();
    public ObservableCollection<PoolInfo> PinnedPools { get; } = new();
    [ObservableProperty] private bool _isPoolsLoading = false;
    [ObservableProperty] private string _poolsStatusText = string.Empty;
    [ObservableProperty] private int _poolsProgressCurrent = 0;
    [ObservableProperty] private int _poolsProgressTotal = 0;
    [ObservableProperty] private bool _poolsProgressHasTotal = false;
    public string DownloadAllLabel => IsPoolMode ? "Download Pool" : "Download All";
    public bool ShowPinPoolButton => IsPoolMode && SelectedPool != null && !PinnedPools.Any(p => p.Id == SelectedPool.Id);
    private HashSet<int> _excludedPoolIds = new();
    private CancellationTokenSource? _poolsCts;
    private bool _rebuildScheduled = false;
    // Streaming pool load cancellation source
    private CancellationTokenSource? _poolStreamCts;
    // Unified gallery context cancellation/versioning
    private CancellationTokenSource? _currentSearchCts;
    private int _contextVersion = 0; // increment per new search/pool to ignore stale async results

    // Simple AddRange helper until a BulkObservableCollection is introduced (future sprint)
    private static void AddRange<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        if (target == null) return;
        if (items == null) return;
        foreach (var item in items) target.Add(item);
    }

    public partial class SavedSearch { public string Name { get; set; } = string.Empty; public List<string> IncludeTags { get; set; } = new(); public List<string> ExcludeTags { get; set; } = new(); public int RatingFilterIndex { get; set; } }
    [ObservableProperty] private string _saveSearchName = string.Empty;
    public ObservableCollection<SavedSearch> SavedSearches { get; } = new();
    public Dictionary<string, PlatformHealth> PlatformHealth { get; private set; } = new();
    [ObservableProperty] private int _currentPage = 1;
    partial void OnCurrentPageChanged(int value) { try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) _ = PersistLastSessionAsync(); } catch { } }
    [ObservableProperty] private bool _hasNextPage = false;
    [ObservableProperty] private int _totalCount = 0;
    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => HasNextPage;
    public string PageInfo => $"Page {CurrentPage}{(TotalCount > 0 ? $" • {TotalCount} total" : string.Empty)}";

    // Gallery sizing
    public double GalleryTileWidth => 180 * GalleryScale;
    public double GalleryTileHeight => 210 * GalleryScale;
    public double GalleryImageSize => 170 * GalleryScale;
    public double GalleryFontSize => 12 * GalleryScale;
    private double _galleryScale = 1.0;
    public double GalleryScale { get => _galleryScale; set { if (Math.Abs(_galleryScale - value) > 0.0001) { _galleryScale = value; OnPropertyChanged(nameof(GalleryScale)); OnPropertyChanged(nameof(GalleryTileWidth)); OnPropertyChanged(nameof(GalleryTileHeight)); OnPropertyChanged(nameof(GalleryImageSize)); OnPropertyChanged(nameof(GalleryFontSize)); } } }

    public MainViewModel(IUnifiedApiService apiService, IDownloadService downloadService, ISettingsService settingsService, ILogger<MainViewModel> logger, IEnumerable<IPlatformApi> platformApis, IThumbnailCacheService thumbCache, ICpuWorkQueue cpuQueue, IPoolsCacheStore cacheStore, IPoolPruningService pruningService, IPlatformShellService? shell = null)
    {
        _apiService = apiService; _downloadService = downloadService; _settingsService = settingsService; _logger = logger; _thumbCache = thumbCache; _cpuQueue = cpuQueue; _cacheStore = cacheStore; _shell = shell;
    _pruningService = pruningService;
    foreach (var p in platformApis) { _apiService.RegisterPlatform(p); if (string.Equals(p.PlatformName, "e621", StringComparison.OrdinalIgnoreCase)) { _e621Platform = p; } }
    _downloadService.DownloadStatusChanged += OnDownloadStatusChanged; _downloadService.DownloadProgressUpdated += OnDownloadProgressUpdated;
    // Populate downloads panel with any persisted jobs on startup
    _ = Task.Run(RefreshDownloadQueueAsync);
    LoadSettings();
    GalleryScale = _settingsService.GetSetting<double>("GalleryScale", 1.0);
        UpdateFavoritesVisibility();
        _ = Task.Run(() => AuthenticatePlatformsAsync(platformApis));
        _ = Task.Run(CheckPlatformHealthAsync);
        // Pools: load from SQLite cache on startup; refresh only if empty
        // Synchronously load from SQLite so the list appears instantly; background work only if needed
        try { _cacheStore.InitializeAsync().GetAwaiter().GetResult(); } catch { }
        // Migration guard: if DB has meta timestamp but zero pools, force a rebuild
        try
        {
            var metaSaved = _cacheStore.GetPoolsSavedAtAsync().GetAwaiter().GetResult();
            if (metaSaved.HasValue)
            {
                var existing = _cacheStore.GetAllPoolsAsync().GetAwaiter().GetResult() ?? new();
                if (existing.Count == 0)
                {
                    _logger.LogInformation("Pools cache meta present but no pool rows found; auto-rebuilding cache");
                    _poolsCacheLastSavedUtc = DateTime.MinValue; // ensure full refresh path
                    _rebuildScheduled = true;
                    try { Dispatcher.UIThread.Post(() => PoolsStatusText = "pools cache invalid — rebuilding…"); } catch { }
                    _ = Task.Run(RefreshPoolsIfStaleAsync);
                }
            }
        }
        catch { }
        // Load pinned pools from SQLite
        try { var pinned = _cacheStore.GetPinnedPoolsAsync().GetAwaiter().GetResult() ?? new(); PinnedPools.Clear(); foreach (var p in pinned) PinnedPools.Add(p); } catch { }
        bool hadCachedStartup = false;
        try { hadCachedStartup = LoadPoolsFromDbAsync().GetAwaiter().GetResult(); }
        catch { }
        if (!hadCachedStartup)
        {
            // No cache -> do first-time full fetch (async) unless already scheduled by guard
            if (!_rebuildScheduled) _ = Task.Run(RefreshPoolsIfStaleAsync);
        }
        else
        {
            // Cache exists -> kick a light incremental update in background only if we have a recent timestamp
            _ = Task.Run(() => IncrementalUpdatePoolsAsync(TimeSpan.FromMinutes(Math.Max(5, _settingsService.GetSetting<int>("PoolsUpdateIntervalMinutes", 360)))));
        }
    // Restore last session only if enabled in settings
    try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) { _ = RestoreLastSessionAsync(); } } catch { _ = RestoreLastSessionAsync(); }
    WeakReferenceMessenger.Default.Register<PoolsCacheRebuiltMessage>(this, async (_, __) => { try { _ = await LoadPoolsFromDbAsync(); Dispatcher.UIThread.Post(() => ApplyPoolsFilter()); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to update pools after cache rebuild notification"); } });
        WeakReferenceMessenger.Default.Register<PoolsCacheRebuildRequestedMessage>(this, async (_, __) => { try { Dispatcher.UIThread.Post(() => { Pools.Clear(); FilteredPools.Clear(); PoolsStatusText = "rebuilding cache…"; }); var file = GetPoolsCacheFilePath(); try { if (File.Exists(file)) File.Delete(file); } catch { } _poolsCacheLastSavedUtc = DateTime.MinValue; await RefreshPoolsIfStaleAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to rebuild pools cache on request"); } });
    WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (_, __) => { try { UpdateFavoritesVisibility(); GalleryScale = _settingsService.GetSetting<double>("GalleryScale", GalleryScale); } catch { } });
        WeakReferenceMessenger.Default.Register<PoolsSoftRefreshRequestedMessage>(this, async (_, __) => { try { await SoftRefreshPoolsAsync(); } catch { } });
    }

    private void LoadSettings()
    {
        try
        {
            // Load Saved Searches
            var json = _settingsService.GetSetting<string>("SavedSearches", null);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var list = JsonSerializer.Deserialize<List<SavedSearch>>(json) ?? new();
                SavedSearches.Clear();
                foreach (var s in list) SavedSearches.Add(s);
            }
        }
        catch { }
    }

    public bool IsSelectedDownloaded { get { var item = SelectedMedia; if (item == null) return false; var defaultDir = _settingsService.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); var hasPoolContext = (item.TagCategories != null && (item.TagCategories.ContainsKey("page_number") || item.TagCategories.ContainsKey("pool_name"))) || IsPoolMode; var template = hasPoolContext ? (_settingsService.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") : (_settingsService.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}"); string Sanitize(string s) { var invalid = Path.GetInvalidFileNameChars(); var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()); return clean.Replace(" ", "_"); } var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension; var rel = template.Replace("{source}", item.Source).Replace("{artist}", Sanitize(item.Artist)).Replace("{id}", item.Id).Replace("{safeTitle}", Sanitize(item.Title)).Replace("{ext}", ext).Replace("{pool_name}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : (SelectedPool?.Name ?? string.Empty))).Replace("{page_number}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty)); var fullPath = Path.Combine(defaultDir, rel); if (File.Exists(fullPath)) return true; try { var poolsRoot = Path.Combine(defaultDir, item.Source, "pools", Sanitize(item.Artist)); if (Directory.Exists(poolsRoot)) { bool match(string file) { var name = Path.GetFileNameWithoutExtension(file); return name != null && (name.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + item.Id, StringComparison.OrdinalIgnoreCase) || name.Contains(item.Id, StringComparison.OrdinalIgnoreCase)); } foreach (var file in Directory.EnumerateFiles(poolsRoot, "*", SearchOption.AllDirectories)) { if (match(file)) return true; } } } catch { } return false; } }

    private static string? TryGetExtensionFromUrl(string? url) { try { if (string.IsNullOrWhiteSpace(url)) return null; var uri = new Uri(url); var ext = Path.GetExtension(uri.AbsolutePath).Trim('.').ToLowerInvariant(); return string.IsNullOrEmpty(ext) ? null : ext; } catch { return null; } }

    private async Task AuthenticatePlatformsAsync(IEnumerable<IPlatformApi> platformApis)
    {
    string BuildUa() { var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"; var euserLocal = _settingsService.GetSetting<string>("E621Username", "") ?? ""; var uname = string.IsNullOrWhiteSpace(euserLocal) ? "Anon" : euserLocal.Trim(); return $"Furchive/{version} (by {uname})"; }
    var ua = BuildUa(); var euser = _settingsService.GetSetting<string>("E621Username", "") ?? ""; var ekey = _settingsService.GetSetting<string>("E621ApiKey", "")?.Trim() ?? "";
        foreach (var p in platformApis)
        {
            try { if (p.PlatformName == "e621") { var creds = new Dictionary<string, string> { ["UserAgent"] = ua }; if (!string.IsNullOrWhiteSpace(euser)) creds["Username"] = euser; if (!string.IsNullOrWhiteSpace(ekey)) creds["ApiKey"] = ekey; await p.AuthenticateAsync(creds); } }
            catch (Exception ex) { _logger.LogWarning(ex, "Auth bootstrap for {Platform} failed", p.PlatformName); }
        }
    }

    [RelayCommand] private async Task SearchAsync()
    {
        if (IsSearching) return;
        try { IsPoolMode = false; CurrentPoolId = null; await PerformSearchAsync(1, reset: true); }
    catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; _logger.LogError(ex, "Search failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Search failed", ex.Message)); } catch { } }
    finally { try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { } }
    }

    [RelayCommand]
    private async Task ClearTagsAsync()
    {
        try
        {
            IncludeTags.Clear();
            ExcludeTags.Clear();
            StatusMessage = "Tags cleared";
            if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync();
        }
        catch { }
    }

    [RelayCommand] private async Task NextPageAsync() { if (!CanGoNext || IsSearching) return; try { if (IsPoolMode && CurrentPoolId.HasValue) { await PerformPoolPageAsync(CurrentPoolId.Value, CurrentPage + 1, reset: true); } else { await PerformSearchAsync(CurrentPage + 1, reset: true); } } catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; _logger.LogError(ex, "Next page failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Next page failed", ex.Message)); } catch { } } finally { try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { } } }
    [RelayCommand] private async Task PrevPageAsync() { if (!CanGoPrev || IsSearching) return; try { if (IsPoolMode && CurrentPoolId.HasValue) { await PerformPoolPageAsync(CurrentPoolId.Value, CurrentPage - 1, reset: true); } else { await PerformSearchAsync(CurrentPage - 1, reset: true); } } catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; _logger.LogError(ex, "Prev page failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Prev page failed", ex.Message)); } catch { } } finally { try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { } } }

    private async Task PerformSearchAsync(int page, bool reset)
    {
        try
        {
            // Cancel any prior search/pool streaming context
            _currentSearchCts?.Cancel();
            _currentSearchCts = new CancellationTokenSource();
            var token = _currentSearchCts.Token;
            var localVersion = System.Threading.Interlocked.Increment(ref _contextVersion);
            if (Dispatcher.UIThread.CheckAccess()) { IsSearching = true; StatusMessage = "Searching..."; if (reset) { SearchResults.Clear(); ClearAggregatedTagCategories(); } }
            else { await Dispatcher.UIThread.InvokeAsync(() => { IsSearching = true; StatusMessage = "Searching..."; if (reset) { SearchResults.Clear(); ClearAggregatedTagCategories(); } }); }

            await EnsureE621AuthAsync();
            var sources = new List<string>(); if (IsE621Enabled) sources.Add("e621"); if (!sources.Any()) sources.Add("e621");
            var includeTags = IncludeTags.ToList(); var excludeTags = ExcludeTags.ToList();
            // Virtual tag handling: translate no_artist -> arttags:0
            ProcessVirtualNoArtist(ref includeTags, ref excludeTags);
            var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } };
            var searchParams = new SearchParameters { IncludeTags = includeTags, ExcludeTags = excludeTags, Sources = sources, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = page, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) };
            var result = await _apiService.SearchAsync(searchParams);
            if (token.IsCancellationRequested || localVersion != _contextVersion) return; // stale
            var filteredItems = result.Items; // API applied arttags:0 filter
            if (Dispatcher.UIThread.CheckAccess()) {
                if (token.IsCancellationRequested || localVersion != _contextVersion) return; // stale
                if (filteredItems.Count > 0)
                {
                    // Batch add to reduce CollectionChanged churn
                    var toAdd = new List<MediaItem>(filteredItems.Count);
                    foreach (var item in filteredItems)
                    {
                        if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) item.PreviewUrl = item.FullImageUrl;
                        toAdd.Add(item);
                        AddItemTagCategories(item);
                    }
                    AddRange(SearchResults, toAdd);
                }
                OnPropertyChanged(nameof(AggregatedTagCategories));
                CurrentPage = page; HasNextPage = result.HasNextPage; TotalCount = result.TotalCount; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo));
            }
            else { await Dispatcher.UIThread.InvokeAsync(() => {
                if (token.IsCancellationRequested || localVersion != _contextVersion) return; // stale
                if (filteredItems.Count > 0)
                {
                    var toAdd = new List<MediaItem>(filteredItems.Count);
                    foreach (var item in filteredItems)
                    {
                        if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) item.PreviewUrl = item.FullImageUrl;
                        toAdd.Add(item);
                        AddItemTagCategories(item);
                    }
                    AddRange(SearchResults, toAdd);
                }
                OnPropertyChanged(nameof(AggregatedTagCategories));
                CurrentPage = page; HasNextPage = result.HasNextPage; TotalCount = result.TotalCount; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo)); }); }
            var status = result.Errors.Any() ? $"Found {filteredItems.Count} items (errors: {string.Join(", ", result.Errors.Keys)})" : $"Found {filteredItems.Count} items"; if (Dispatcher.UIThread.CheckAccess()) StatusMessage = status; else await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = status);
            try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { }
        }
        finally { if (Dispatcher.UIThread.CheckAccess()) IsSearching = false; else await Dispatcher.UIThread.InvokeAsync(() => IsSearching = false); }
    }

    // Guard to prevent concurrent append calls triggered by rapid scroll events
    private int _appendInFlight = 0;

    // Append next page into SearchResults without clearing
    public async Task<bool> AppendNextPageAsync()
    {
        if (!HasNextPage) return false;
        if (System.Threading.Interlocked.CompareExchange(ref _appendInFlight, 1, 0) != 0) return false;
        try
        {
            IsSearching = true;
            await EnsureE621AuthAsync();
            var sources = new List<string>(); if (IsE621Enabled) sources.Add("e621"); if (!sources.Any()) sources.Add("e621");
            var includeTags = IncludeTags.ToList();
            var excludeTags = ExcludeTags.ToList();
            ProcessVirtualNoArtist(ref includeTags, ref excludeTags);
            var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } };
            var nextPage = CurrentPage + 1;
            var searchParams = new SearchParameters { IncludeTags = includeTags, ExcludeTags = excludeTags, Sources = sources, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = nextPage, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) };
            var result = await _apiService.SearchAsync(searchParams);
            var filteredItems = result.Items; // API applied arttags:0 filter
            if (filteredItems.Count > 0)
            {
                var toAdd = new List<MediaItem>(filteredItems.Count);
                foreach (var item in filteredItems)
                {
                    if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) item.PreviewUrl = item.FullImageUrl;
                    toAdd.Add(item);
                    AddItemTagCategories(item);
                }
                AddRange(SearchResults, toAdd);
            }
            OnPropertyChanged(nameof(AggregatedTagCategories));
            CurrentPage = nextPage;
            HasNextPage = result.HasNextPage;
            TotalCount = Math.Max(TotalCount, result.TotalCount);
            OnPropertyChanged(nameof(CanGoPrev));
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(PageInfo));
            return result.Items.Any();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Append next page failed");
            StatusMessage = $"Append failed: {ex.Message}";
            try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Append failed", ex.Message)); } catch { }
            return false;
        }
    finally { IsSearching = false; System.Threading.Interlocked.Exchange(ref _appendInFlight, 0); }
    }

    // Viewer-specific append: fetch next page items without advancing CurrentPage to avoid altering gallery panel state.
    public async Task<bool> AppendNextPageForViewerAsync()
    {
        if (!HasNextPage) return false;
        if (System.Threading.Interlocked.CompareExchange(ref _appendInFlight, 1, 0) != 0) return false;
        try
        {
            // Do not set IsSearching to true (avoid UI spinner change); operate silently.
            await EnsureE621AuthAsync();
            var sources = new List<string>(); if (IsE621Enabled) sources.Add("e621"); if (!sources.Any()) sources.Add("e621");
            var includeTags = IncludeTags.ToList();
            var excludeTags = ExcludeTags.ToList();
            ProcessVirtualNoArtist(ref includeTags, ref excludeTags);
            var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } };
            var nextPage = CurrentPage + 1; // do NOT assign to CurrentPage
            var searchParams = new SearchParameters { IncludeTags = includeTags, ExcludeTags = excludeTags, Sources = sources, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = nextPage, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) };
            var result = await _apiService.SearchAsync(searchParams);
            var filteredItems = result.Items; // API applied arttags:0 filter
            if (filteredItems.Count > 0)
            {
                var toAdd = new List<MediaItem>(filteredItems.Count);
                foreach (var item in filteredItems)
                {
                    if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) item.PreviewUrl = item.FullImageUrl;
                    toAdd.Add(item);
                    AddItemTagCategories(item);
                }
                AddRange(SearchResults, toAdd);
            }
            OnPropertyChanged(nameof(AggregatedTagCategories));
            // Update HasNextPage & TotalCount but leave CurrentPage untouched for gallery UI stability.
            HasNextPage = result.HasNextPage;
            TotalCount = Math.Max(TotalCount, result.TotalCount);
            OnPropertyChanged(nameof(CanGoNext));
            OnPropertyChanged(nameof(PageInfo)); // total count might change
            return result.Items.Any();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Viewer append next page failed");
            return false;
        }
        finally { System.Threading.Interlocked.Exchange(ref _appendInFlight, 0); }
    }

    private async Task PrefetchNextPagesAsync(int currentPage)
    {
        try
        {
            var ahead = Math.Clamp(_settingsService.GetSetting<int>("E621SearchPrefetchPagesAhead", 2), 0, 5); if (ahead <= 0) return;
            await EnsureE621AuthAsync(); var includeTags = IncludeTags.ToList(); var excludeTags = ExcludeTags.ToList(); var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } }; var limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50);
            var pages = Enumerable.Range(currentPage + 1, ahead).ToList(); if (pages.Count == 0) return;
            await Dispatcher.UIThread.InvokeAsync(() => { IsBackgroundCaching = true; BackgroundCachingCurrent = 0; BackgroundCachingTotal = pages.Count; BackgroundCachingItemsFetched = 0; });
            var degree = Math.Clamp(_settingsService.GetSetting<int>("E621SearchPrefetchParallelism", 2), 1, 4); using var throttler = new SemaphoreSlim(degree); var tasks = new List<Task>();
            foreach (var p in pages) { await throttler.WaitAsync(); var task = Task.Run(async () => { try { var sr = await _apiService.SearchAsync(new SearchParameters { IncludeTags = includeTags, ExcludeTags = excludeTags, Sources = new List<string> { "e621" }, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = p, Limit = limit }); await Dispatcher.UIThread.InvokeAsync(() => { BackgroundCachingItemsFetched += sr.Items?.Count ?? 0; }); } catch (Exception ex) { _logger.LogDebug(ex, "Prefetch page {Page} failed", p); } finally { await Dispatcher.UIThread.InvokeAsync(() => { BackgroundCachingCurrent++; OnPropertyChanged(nameof(BackgroundCachingPercent)); }); throttler.Release(); } }); tasks.Add(task); }
            await Task.WhenAll(tasks);
        }
        catch { }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { IsBackgroundCaching = false; BackgroundCachingTotal = 0; BackgroundCachingCurrent = 0; BackgroundCachingItemsFetched = 0; OnPropertyChanged(nameof(BackgroundCachingPercent)); }); }
    }

    private async Task EnsureE621AuthAsync()
    { try { if (_e621Platform == null) return; string BuildUaLocal() { var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"; var euserLocal = _settingsService.GetSetting<string>("E621Username", "") ?? ""; var uname = string.IsNullOrWhiteSpace(euserLocal) ? "Anon" : euserLocal.Trim(); return $"Furchive/{version} (by {uname})"; } var ua = BuildUaLocal(); var euser = _settingsService.GetSetting<string>("E621Username", "") ?? ""; var ekey = _settingsService.GetSetting<string>("E621ApiKey", "")?.Trim() ?? ""; var creds = new Dictionary<string, string> { ["UserAgent"] = ua }; if (!string.IsNullOrWhiteSpace(euser)) creds["Username"] = euser; if (!string.IsNullOrWhiteSpace(ekey)) creds["ApiKey"] = ekey; await _e621Platform.AuthenticateAsync(creds); } catch { } }

    private async Task EnsurePreviewPoolInfoAsync(MediaItem? item)
    {
        try
        {
            if (item == null) { PreviewPoolName = string.Empty; OnPropertyChanged(nameof(PreviewPoolDisplayName)); OnPropertyChanged(nameof(PreviewPoolVisible)); return; }
            if (item.TagCategories != null && (item.TagCategories.ContainsKey("pool_name") || item.TagCategories.ContainsKey("pool_id")))
            {
                if (item.TagCategories.TryGetValue("pool_name", out var names) && names.Count > 0) { PreviewPoolName = names[0]; } else { PreviewPoolName = string.Empty; }
                try { PoolInfo? pool = null; if (item.TagCategories.TryGetValue("pool_id", out var ids) && ids.Count > 0 && int.TryParse(ids[0], out var pid)) { pool = Pools.FirstOrDefault(p => p.Id == pid) ?? new PoolInfo { Id = pid, Name = PreviewPoolName ?? string.Empty }; } else if (!string.IsNullOrWhiteSpace(PreviewPoolName)) { pool = Pools.FirstOrDefault(p => string.Equals(p.Name, PreviewPoolName, StringComparison.OrdinalIgnoreCase)) ?? new PoolInfo { Id = 0, Name = PreviewPoolName }; } if (pool != null) { SelectedPool = pool; } } catch { }
                OnPropertyChanged(nameof(PreviewPoolDisplayName)); OnPropertyChanged(nameof(PreviewPoolVisible)); return;
            }
            if (_e621Platform != null && string.Equals(item.Source, "e621", StringComparison.OrdinalIgnoreCase))
            {
                try { var ctx = await _e621Platform.GetPoolContextForPostAsync(item.Id); if (ctx.HasValue) { var (poolId, poolName, pageNumber) = ctx.Value; item.TagCategories ??= new Dictionary<string, List<string>>(); item.TagCategories["pool_id"] = new List<string> { poolId.ToString() }; item.TagCategories["pool_name"] = new List<string> { poolName }; if (pageNumber > 0) item.TagCategories["page_number"] = new List<string> { pageNumber.ToString("D5") }; var sp = Pools.FirstOrDefault(p => p.Id == poolId) ?? new PoolInfo { Id = poolId, Name = poolName }; if (sp != null) SelectedPool = sp; PreviewPoolName = poolName; OnPropertyChanged(nameof(PreviewPoolDisplayName)); OnPropertyChanged(nameof(PreviewPoolVisible)); return; } } catch { }
            }
            PreviewPoolName = string.Empty; OnPropertyChanged(nameof(PreviewPoolDisplayName)); OnPropertyChanged(nameof(PreviewPoolVisible));
        }
        catch { }
    }

    private void ApplyPoolsFilter()
    {
        try { FilteredPools.Clear(); if (string.IsNullOrWhiteSpace(PoolSearch)) { foreach (var p in Pools.Take(1000)) FilteredPools.Add(p); PoolsStatusText = $"{FilteredPools.Count} pools"; return; } var q = PoolSearch.Trim(); bool isNumber = int.TryParse(q, out var id); foreach (var p in Pools) { if (isNumber) { if (p.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)) FilteredPools.Add(p); } else if (p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) { FilteredPools.Add(p); } if (FilteredPools.Count >= 1000) break; } PoolsStatusText = $"{FilteredPools.Count} pools"; } catch { }
    }

    [RelayCommand] private async Task PinSelectedPoolAsync() {
        try {
            if (!IsPoolMode) return; var pool = SelectedPool; if (pool == null) return;
            if (!PinnedPools.Any(p => p.Id == pool.Id)) {
                PinnedPools.Add(new PoolInfo { Id = pool.Id, Name = pool.Name, PostCount = pool.PostCount });
                await PersistPinnedPoolsAsync();
                // Scroll pinned pools list to bottom (view layer will find control by name)
                try {
                    Dispatcher.UIThread.Post(() => {
                        try {
                            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null) {
                                var lb = FindDescendantListBoxByName(desktop.MainWindow, "PinnedPoolsList");
                                if (lb != null && lb.ItemCount > 0) {
                                    var last = PinnedPools.LastOrDefault();
                                    if (last != null) lb.ScrollIntoView(last);
                                }
                            }
                        } catch { }
                    });
                } catch { }
            }
        } catch { }
    }

    private static ListBox? FindDescendantListBoxByName(Control root, string name)
    {
        try {
            if (root is ListBox lb && lb.Name == name) return lb;
            foreach (var child in root.GetVisualDescendants())
            {
                if (child is ListBox lb2 && lb2.Name == name) return lb2;
            }
        } catch { }
        return null;
    }
    [RelayCommand] private async Task UnpinPoolAsync(PoolInfo? pool) { if (pool == null) return; try { var existing = PinnedPools.FirstOrDefault(p => p.Id == pool.Id); if (existing != null) PinnedPools.Remove(existing); await PersistPinnedPoolsAsync(); } catch { } }
    private async Task PersistPinnedPoolsAsync() { try { await _cacheStore.SavePinnedPoolsAsync(PinnedPools.ToList()); } catch { } }
    private string GetPoolsCacheFilePath() => Path.Combine(_cacheDir, "pools_cache.sqlite");

    private async Task<bool> LoadPoolsFromDbAsync()
    {
        try
        {
            var list = await _cacheStore.GetAllPoolsAsync();
            if (list != null && list.Any())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Pools.Clear();
                    foreach (var p in list)
                    {
                        if (!p.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && p.PostCount > 0) Pools.Add(p);
                    }
                    ApplyPoolsFilter();
                    PoolsStatusText = $"{Pools.Count} pools";
                });
                // Background prune pools whose posts are all deleted
                _ = Task.Run(PruneZeroVisiblePoolsAsync);
                var saved = await _cacheStore.GetPoolsSavedAtAsync();
                _poolsCacheLastSavedUtc = saved ?? DateTime.UtcNow.AddDays(-7);
                return true;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load pools from DB"); }
        return false;
    }

    private async Task RefreshPoolsIfStaleAsync()
    {
        try
        {
            // If DB already has pools loaded in-memory or on disk, skip full refresh
            if (Pools.Any()) { return; }
            try { var existing = await _cacheStore.GetAllPoolsAsync(); if (existing != null && existing.Any()) { Dispatcher.UIThread.Post(() => { Pools.Clear(); foreach (var p in existing) { if (!p.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && p.PostCount > 0) Pools.Add(p); } ApplyPoolsFilter(); PoolsStatusText = $"{Pools.Count} pools"; }); return; } } catch { }
            IsPoolsLoading = true;
            _poolsCts?.Cancel();
            _poolsCts = new CancellationTokenSource();
            PoolsProgressCurrent = 0; PoolsProgressTotal = 0; PoolsProgressHasTotal = false; PoolsStatusText = "(0) updating…";
            var progress = new Progress<(int current, int? total)>(tuple =>
            {
                PoolsProgressCurrent = tuple.current; PoolsProgressHasTotal = tuple.total.HasValue; PoolsProgressTotal = tuple.total ?? 0;
                PoolsStatusText = PoolsProgressHasTotal ? $"({PoolsProgressCurrent}/{PoolsProgressTotal}) updating…" : $"({PoolsProgressCurrent}) updating…";
            });
            var list = await _apiService.GetPoolsAsync("e621", progress, _poolsCts.Token);
            list = list.Where(p => !p.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && p.PostCount > 0).ToList();
            var snapshot = list.ToList();
            Dispatcher.UIThread.Post(() => { Pools.Clear(); foreach (var p in snapshot) Pools.Add(p); ApplyPoolsFilter(); PoolsStatusText = $"{Pools.Count} pools"; });
            _ = Task.Run(PruneZeroVisiblePoolsAsync);
            try { await _cacheStore.UpsertPoolsAsync(snapshot, CancellationToken.None); var saved = await _cacheStore.GetPoolsSavedAtAsync(); _poolsCacheLastSavedUtc = saved ?? DateTime.UtcNow; } catch { }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to refresh pools"); }
        finally { IsPoolsLoading = false; }
    }

    private DateTime _poolsCacheLastSavedUtc = DateTime.MinValue;
    private async Task IncrementalUpdatePoolsAsync(TimeSpan interval)
    {
        try
        {
            PoolsStatusText = "checking updates…";
            var since = _poolsCacheLastSavedUtc == DateTime.MinValue ? DateTime.UtcNow.AddDays(-7) : _poolsCacheLastSavedUtc;
            var updates = await _apiService.GetPoolsUpdatedSinceAsync("e621", since);
            if (updates == null || updates.Count == 0) { PoolsStatusText = $"{Pools.Count} pools"; return; }
            // Build the updated list first, then write it to DB and UI
            var current = Pools.ToDictionary(p => p.Id);
            foreach (var u in updates)
            {
                if (!u.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && u.PostCount > 0) { current[u.Id] = u; }
                else { current.Remove(u.Id); }
            }
            var updated = current.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                Pools.Clear();
                foreach (var p in updated) Pools.Add(p);
                ApplyPoolsFilter();
                PoolsStatusText = $"{Pools.Count} pools";
            });
            _ = Task.Run(PruneZeroVisiblePoolsAsync);
            try { await _cacheStore.UpsertPoolsAsync(updated, CancellationToken.None); var saved = await _cacheStore.GetPoolsSavedAtAsync(); _poolsCacheLastSavedUtc = saved ?? DateTime.UtcNow; } catch { }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Incremental pool update failed"); }
        finally { /* no auto-reschedule; run only when explicitly requested */ }
    }

    private async Task StartOrKickIncrementalAsync() { try { var file = GetPoolsCacheFilePath(); if (!File.Exists(file) || !Pools.Any()) { await RefreshPoolsIfStaleAsync(); return; } } catch { } /* Startup no longer auto-runs incremental. This method retained for compatibility if invoked elsewhere. */ }
    [RelayCommand] private void CancelPoolsUpdate() { try { _poolsCts?.Cancel(); } catch { } }
    [RelayCommand] private void RunPoolsFilter() => ApplyPoolsFilter();

    // Background validation: remove pools whose post list is now empty (all posts deleted)
    private async Task PruneZeroVisiblePoolsAsync()
    {
    if (_e621Platform == null) return;
    List<PoolInfo> snapshot; try { snapshot = Pools.ToList(); } catch { return; }
    if (snapshot.Count == 0) return;
    List<int> removedIds;
    try { removedIds = await _pruningService.DeterminePoolsToPruneAsync(_e621Platform, snapshot, CancellationToken.None); }
    catch { return; }
    if (removedIds == null || removedIds.Count == 0) return;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (int i = Pools.Count - 1; i >= 0; i--)
                {
                    if (removedIds.Contains(Pools[i].Id)) Pools.RemoveAt(i);
                }
                ApplyPoolsFilter();
                PoolsStatusText = $"{Pools.Count} pools";
            });
            // Persist pruned list
            try { await _cacheStore.UpsertPoolsAsync(Pools.ToList(), CancellationToken.None); } catch { }
        }
        catch { }
    }

    [RelayCommand] private async Task LoadSelectedPoolAsync(PoolInfo? fromPinned = null)
    {
        var pool = fromPinned ?? SelectedPool; if (pool == null || IsSearching) return;
        try
        {
            // Cancel any prior search context (search or other pool streaming)
            _currentSearchCts?.Cancel();
            _currentSearchCts = new CancellationTokenSource();
            var token = _currentSearchCts.Token;
            var localVersion = System.Threading.Interlocked.Increment(ref _contextVersion);
            IsSearching = true; StatusMessage = $"Loading pool {pool.Id} ({pool.Name})..."; SearchResults.Clear(); CurrentPage = 1;
            await EnsureE621AuthAsync(); IsPoolMode = true; CurrentPoolId = pool.Id; SelectedPool = Pools.FirstOrDefault(p => p.Id == pool.Id) ?? pool;

            // Cancel any prior streaming (covers cached + fresh phases)
            _poolStreamCts?.Cancel();
            _poolStreamCts = new CancellationTokenSource();
            var streamToken = CancellationTokenSource.CreateLinkedTokenSource(_poolStreamCts.Token, token).Token;

            var cached = await _cacheStore.GetPoolPostsAsync(pool.Id);
            int cachedTotal = cached?.Count ?? 0;
            TotalCount = cachedTotal; OnPropertyChanged(nameof(PageInfo));

            // Add first batch immediately so UI shows content fast
            if (cachedTotal > 0 && !token.IsCancellationRequested && localVersion == _contextVersion)
            {
                var firstSlice = cached!.Take(5).ToList();
                var poolNameLocal = pool.Name;
                foreach (var m in firstSlice)
                {
                    var idx = SearchResults.Count + 1;
                    var pageNum = idx.ToString("D5");
                    m.TagCategories ??= new Dictionary<string, List<string>>();
                    m.TagCategories["pool_name"] = new List<string> { poolNameLocal };
                    m.TagCategories["page_number"] = new List<string> { pageNum };
                    if (string.IsNullOrWhiteSpace(m.PreviewUrl) && !string.IsNullOrWhiteSpace(m.FullImageUrl)) m.PreviewUrl = m.FullImageUrl;
                    SearchResults.Add(m);
                }
                if (!token.IsCancellationRequested && localVersion == _contextVersion)
                    StatusMessage = $"Loaded {Math.Min(5, cachedTotal)} / {cachedTotal} cached…";
            }
            else
            {
                StatusMessage = "Loading pool posts… (streaming)";
            }

            // Background task to stream remaining cached items (if any) then fetch & stream fresh updates
            _ = Task.Run(async () =>
            {
                try
                {
                    var poolNameLocal = pool.Name;
                    // Stream remaining cached items
                    if (cachedTotal > 5 && !streamToken.IsCancellationRequested && localVersion == _contextVersion)
                    {
                        for (int i = 5; i < cachedTotal; i += 5)
                        {
                            if (streamToken.IsCancellationRequested) return;
                            var slice = cached!.Skip(i).Take(5).ToList();
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (streamToken.IsCancellationRequested || localVersion != _contextVersion) return; // stale
                                foreach (var m in slice)
                                {
                                    var idx = SearchResults.Count + 1;
                                    var pageNum = idx.ToString("D5");
                                    m.TagCategories ??= new Dictionary<string, List<string>>();
                                    m.TagCategories["pool_name"] = new List<string> { poolNameLocal };
                                    m.TagCategories["page_number"] = new List<string> { pageNum };
                                    if (string.IsNullOrWhiteSpace(m.PreviewUrl) && !string.IsNullOrWhiteSpace(m.FullImageUrl)) m.PreviewUrl = m.FullImageUrl;
                                    SearchResults.Add(m);
                                }
                                if (!streamToken.IsCancellationRequested && localVersion == _contextVersion)
                                    StatusMessage = $"Loaded {SearchResults.Count} / {cachedTotal} cached…";
                            });
                            await Task.Delay(35, streamToken);
                        }
                    }

                    // After cached items streamed, fetch fresh full list to detect new additions; stream only new tail beyond cached count
                    var fresh = await _apiService.GetAllPoolPostsAsync("e621", pool.Id, streamToken) ?? new List<MediaItem>();
                    if (streamToken.IsCancellationRequested) return;
                    try { await _cacheStore.UpsertPoolPostsAsync(pool.Id, fresh); } catch { }
                    if (fresh.Count > cachedTotal && !streamToken.IsCancellationRequested && localVersion == _contextVersion)
                    {
                        for (int i = cachedTotal; i < fresh.Count; i += 5)
                        {
                            if (streamToken.IsCancellationRequested) return;
                            var slice = fresh.Skip(i).Take(5).ToList();
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                if (streamToken.IsCancellationRequested || localVersion != _contextVersion) return; // stale
                                foreach (var m in slice)
                                {
                                    var idx = SearchResults.Count + 1;
                                    var pageNum = idx.ToString("D5");
                                    m.TagCategories ??= new Dictionary<string, List<string>>();
                                    m.TagCategories["pool_name"] = new List<string> { poolNameLocal };
                                    m.TagCategories["page_number"] = new List<string> { pageNum };
                                    if (string.IsNullOrWhiteSpace(m.PreviewUrl) && !string.IsNullOrWhiteSpace(m.FullImageUrl)) m.PreviewUrl = m.FullImageUrl;
                                    SearchResults.Add(m);
                                }
                                if (localVersion == _contextVersion && !streamToken.IsCancellationRequested)
                                {
                                    TotalCount = fresh.Count; OnPropertyChanged(nameof(PageInfo));
                                    StatusMessage = $"Loaded {SearchResults.Count} / {fresh.Count} (updated)";
                                }
                            });
                            await Task.Delay(50, streamToken);
                        }
                    }
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (localVersion != _contextVersion || streamToken.IsCancellationRequested) return; // stale
                        HasNextPage = false; OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(CanGoPrev));
                        TotalCount = Math.Max(fresh.Count, cachedTotal); OnPropertyChanged(nameof(PageInfo));
                        StatusMessage = $"Loaded pool {pool.Id}: {SearchResults.Count} items";
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.LogWarning(ex, "Streaming pool load failed for {PoolId}", pool.Id); }
            }, streamToken);

            try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load pool: {ex.Message}"; _logger.LogError(ex, "Pool load failed");
            try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Load pool failed", ex.Message)); } catch { }
        }
        finally { IsSearching = false; }
    }

    public Task TriggerLoadSelectedPoolAsync() => LoadSelectedPoolAsync(null);
    // Public helper for view layer to load a pinned pool without exposing full command
    public Task LoadPinnedPoolAsync(PoolInfo pool) => LoadSelectedPoolAsync(pool);
    private async Task PerformPoolPageAsync(int poolId, int page, bool reset) { await Task.CompletedTask; }
    [RelayCommand] private async Task SoftRefreshPoolsAsync() { try { var minutes = Math.Max(5, _settingsService.GetSetting<int>("PoolsUpdateIntervalMinutes", 360)); await IncrementalUpdatePoolsAsync(TimeSpan.FromMinutes(minutes)); } catch (Exception ex) { _logger.LogWarning(ex, "Soft refresh pools failed"); } }
    // Persist last session in SQLite via IPoolsCacheStore
    private async Task PersistLastSessionAsync()
    {
        try
        {
            var session = new LastSession
            {
                IsPoolMode = IsPoolMode,
                PoolId = CurrentPoolId,
                Include = IncludeTags.ToList(),
                Exclude = ExcludeTags.ToList(),
                RatingFilterIndex = RatingFilterIndex,
                Page = CurrentPage
            };
            var json = JsonSerializer.Serialize(session);
            await _cacheStore.SaveLastSessionAsync(json);
        }
        catch { }
    }

    private async Task RestoreLastSessionAsync()
    {
        try
        {
            var json = await _cacheStore.LoadLastSessionAsync();
            if (string.IsNullOrWhiteSpace(json)) return;
            var session = JsonSerializer.Deserialize<LastSession>(json);
            if (session == null) return;

            RatingFilterIndex = session.RatingFilterIndex;
            IncludeTags.Clear(); foreach (var t in (session.Include ?? new())) IncludeTags.Add(t);
            ExcludeTags.Clear(); foreach (var t in (session.Exclude ?? new())) ExcludeTags.Add(t);

            if (session.IsPoolMode && session.PoolId.HasValue)
            {
                // Use the existing cached-first loader for pools so we avoid network on startup
                CurrentPoolId = session.PoolId;
                IsPoolMode = true;
                var pool = Pools.FirstOrDefault(p => p.Id == session.PoolId.Value) ?? new PoolInfo { Id = session.PoolId.Value, Name = SelectedPool?.Name ?? $"Pool {session.PoolId.Value}" };
                await LoadSelectedPoolAsync(pool);
                StatusMessage = $"Restored last pool: {session.PoolId}";
                return;
            }
            await PerformSearchAsync(Math.Max(1, session.Page), reset: true);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore last session"); }
    }
    private sealed class LastSession { public bool IsPoolMode { get; set; } public int? PoolId { get; set; } public List<string> Include { get; set; } = new(); public List<string> Exclude { get; set; } = new(); public int RatingFilterIndex { get; set; } public int Page { get; set; } = 1; }
    public async Task<MediaItem?> FetchNextFromApiAsync(bool forward)
    {
        var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } };
    var include = IncludeTags.ToList(); var exclude = ExcludeTags.ToList();
    ProcessVirtualNoArtist(ref include, ref exclude);
        var page = Math.Max(1, CurrentPage + (forward ? 1 : -1));
    var result = await _apiService.SearchAsync(new SearchParameters { IncludeTags = include, ExcludeTags = exclude, Sources = new List<string> { "e621" }, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = page, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) });
    return result.Items.FirstOrDefault();
    }

    // Extracts virtual tag markers and signals filtering requirements.
    private static void ProcessVirtualNoArtist(ref List<string> includeTags, ref List<string> excludeTags)
    {
        // Translate virtual token no_artist into underlying API tag arttags:0 (posts with zero artist tags)
        for (int i = 0; i < includeTags.Count; i++)
        {
            if (string.Equals(includeTags[i], "no_artist", StringComparison.OrdinalIgnoreCase)) includeTags[i] = "arttags:0";
        }
        for (int i = 0; i < excludeTags.Count; i++)
        {
            if (string.Equals(excludeTags[i], "no_artist", StringComparison.OrdinalIgnoreCase)) excludeTags[i] = "arttags:0";
        }
    }

    /// <summary>
    /// Load a pool directly by id (invoked when user clicks pool_name / pool_id tag chip in preview panel).
    /// </summary>
    public async Task LoadPoolByIdAsync(int poolId, string? poolName = null)
    {
        try
        {
            if (poolId <= 0) return;
            // Avoid redundant reload if already showing same pool
            if (IsPoolMode && CurrentPoolId == poolId && SearchResults.Count > 0) return;
            var pool = Pools.FirstOrDefault(p => p.Id == poolId) ?? new PoolInfo { Id = poolId, Name = poolName ?? ($"Pool {poolId}") };
            // Set selection then reuse existing loader
            SelectedPool = pool;
            await LoadSelectedPoolAsync(pool);
        }
        catch { }
    }

    // Resolve categories for newly added include/exclude tags before any search results.
    // Adds them into _aggregatedTagSets so chips colorize immediately. Safe to call concurrently.
    private async Task EnrichTagsWithCategoriesAsync(IEnumerable<string> tags)
    {
        try
        {
            var list = tags.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (list.Count == 0) return;
            var sources = new List<string>(); if (IsE621Enabled) sources.Add("e621"); if (!sources.Any()) sources.Add("e621");
            foreach (var tag in list)
            {
                try
                {
                    // Skip if already present in aggregated sets
                    if (_aggregatedTagSets.Any(kv => kv.Value.Contains(tag))) continue;
                    var cat = await _apiService.GetTagCategoryAsync(tag, sources);
                    if (string.IsNullOrWhiteSpace(cat)) continue;
                    lock (_aggregatedTagSets)
                    {
                        if (!_aggregatedTagSets.TryGetValue(cat, out var set)) { set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); _aggregatedTagSets[cat] = set; }
                        set.Add(tag);
                    }
                }
                catch { }
            }
            // Notify UI once after batch
            try { await Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(nameof(AggregatedTagCategories))); } catch { }
        }
        catch { }
    }
    [RelayCommand] private async Task AddIncludeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        // Split by whitespace to allow multi-tag entry
        var parts = tag.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool changed = false;
        foreach (var p in parts)
        {
            if (!IncludeTags.Contains(p)) { IncludeTags.Add(p); changed = true; }
        }
        if (changed)
        {
            // Fire-and-forget category lookups so chips can colorize before results
            _ = Task.Run(async () => await EnrichTagsWithCategoriesAsync(parts));
            try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { }
        }
    }
    [RelayCommand] private async Task RemoveIncludeTag(string tag) { IncludeTags.Remove(tag); try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { } }
    [RelayCommand] private async Task AddExcludeTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var parts = tag.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        bool changed = false;
        foreach (var p in parts)
        {
            if (!ExcludeTags.Contains(p)) { ExcludeTags.Add(p); changed = true; }
        }
        if (changed)
        {
            _ = Task.Run(async () => await EnrichTagsWithCategoriesAsync(parts));
            try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { }
        }
    }
    [RelayCommand] private async Task RemoveExcludeTag(string tag) { ExcludeTags.Remove(tag); try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { } }
    [RelayCommand] private async Task DownloadSelectedAsync() { if (SelectedMedia == null) return; try { var downloadPath = _settingsService.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); var tempPath = GetTempPathFor(SelectedMedia); var finalPath = GenerateFinalPath(SelectedMedia, downloadPath); Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!); if (File.Exists(tempPath) && !File.Exists(finalPath)) { File.Move(tempPath, finalPath); StatusMessage = $"Saved from temp: {SelectedMedia.Title}"; OnPropertyChanged(nameof(IsSelectedDownloaded)); return; } await _downloadService.QueueDownloadAsync(SelectedMedia, downloadPath); StatusMessage = $"Queued download: {SelectedMedia.Title}"; } catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; _logger.LogError(ex, "Download failed for {Title}", SelectedMedia.Title); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Download failed", ex.Message)); } catch { } } }
    [RelayCommand] private async Task DownloadAllAsync() { if (!SearchResults.Any()) return; try { var downloadPath = _settingsService.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); var toQueue = new List<MediaItem>(); foreach (var m in SearchResults) { var tempPath = GetTempPathFor(m); var finalPath = GenerateFinalPath(m, downloadPath); Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!); if (File.Exists(tempPath) && !File.Exists(finalPath)) { try { File.Move(tempPath, finalPath); } catch { toQueue.Add(m); } } else { toQueue.Add(m); } } if (toQueue.Any()) { if (IsPoolMode && CurrentPoolId.HasValue) { var label = SelectedPool?.Name ?? PreviewPoolName ?? "Pool"; await _downloadService.QueueAggregateDownloadsAsync("pool", toQueue, downloadPath, label); } else { await _downloadService.QueueMultipleDownloadsAsync(toQueue, downloadPath); } } StatusMessage = IsPoolMode ? $"Queued pool downloads ({SearchResults.Count} items)" : $"Queued {SearchResults.Count} downloads"; } catch (Exception ex) { StatusMessage = $"Batch download failed: {ex.Message}"; _logger.LogError(ex, "Batch download failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Batch download failed", ex.Message)); } catch { } } }

    // Queue control commands (optional for bindings)
    [RelayCommand] private async Task PauseJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.PauseDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Pause failed"); } }
    [RelayCommand] private async Task ResumeJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.ResumeDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Resume failed"); } }
    [RelayCommand] private async Task CancelJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.CancelDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Cancel failed"); } }
    [RelayCommand] private async Task RetryJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.RetryDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Retry failed"); } }
    [RelayCommand] private void OpenDownloadFile(DownloadJob? job) { try { if (job == null) return; var path = job.DestinationPath; if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return; _shell?.OpenPath(path); } catch { } }
    [RelayCommand] private void OpenDownloadFolder(DownloadJob? job) { try { if (job == null) return; var path = job.DestinationPath; if (string.IsNullOrWhiteSpace(path)) return; var folder = Path.GetDirectoryName(path); if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return; _shell?.OpenFolder(folder); } catch { } }
    [RelayCommand] private async Task RemoveDownload(DownloadJob? job) { if (job == null) return; try { await _downloadService.RemoveJobAsync(job.Id, deleteFile: false); DownloadQueue.Remove(job); } catch { } }
    [RelayCommand] private async Task DeleteDownload(DownloadJob? job) { if (job == null) return; try { await _downloadService.RemoveJobAsync(job.Id, deleteFile: true); DownloadQueue.Remove(job); } catch { } }
    [RelayCommand] private async Task ClearDownloadsAsync() {
        try {
            // Snapshot to avoid modification during iteration
            var jobs = DownloadQueue.ToList();
            foreach (var j in jobs) {
                try {
                    if (j.Status == DownloadStatus.Downloading || j.Status == DownloadStatus.Queued || j.Status == DownloadStatus.Paused) {
                        try { await _downloadService.CancelDownloadAsync(j.Id); } catch { }
                    }
                    await _downloadService.RemoveJobAsync(j.Id, deleteFile: false);
                } catch { }
            }
            DownloadQueue.Clear();
        } catch { }
    }
    private static string GetTempPathFor(MediaItem item) { var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp"); var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension; var safeArtist = new string((item.Artist ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Replace(" ", "_"); var safeTitle = new string((item.Title ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Replace(" ", "_"); var file = $"{item.Source}_{item.Id}_{safeArtist}_{safeTitle}.{ext}"; return Path.Combine(tempDir, file); }
    private string GenerateFinalPath(MediaItem mediaItem, string basePath) { var hasPoolContext = mediaItem.TagCategories != null && (mediaItem.TagCategories.ContainsKey("page_number") || mediaItem.TagCategories.ContainsKey("pool_name")); var template = hasPoolContext ? (_settingsService.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") : (_settingsService.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}"); var extFinal = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? TryGetExtensionFromUrl(mediaItem.FullImageUrl) ?? "bin" : mediaItem.FileExtension; string Sanitize(string s) { var invalid = Path.GetInvalidFileNameChars(); var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()); return clean.Replace(" ", "_"); } var filenameRel = template.Replace("{source}", mediaItem.Source).Replace("{artist}", Sanitize(mediaItem.Artist)).Replace("{id}", mediaItem.Id).Replace("{safeTitle}", Sanitize(mediaItem.Title)).Replace("{ext}", extFinal).Replace("{pool_name}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : (SelectedPool?.Name ?? string.Empty))).Replace("{page_number}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty)); return Path.Combine(basePath, filenameRel); }
    [RelayCommand] private async Task RefreshDownloadQueueAsync()
    {
        try
        {
            var jobs = await _downloadService.GetDownloadJobsAsync();
            jobs = jobs.Where(j => string.IsNullOrEmpty(j.ParentId)).ToList();

            // All collection mutations must occur on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    var map = DownloadQueue.ToDictionary(j => j.Id);
                    var incomingIds = new HashSet<string>(jobs.Select(j => j.Id));
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
                    for (int i = DownloadQueue.Count - 1; i >= 0; i--)
                    {
                        if (!incomingIds.Contains(DownloadQueue[i].Id)) DownloadQueue.RemoveAt(i);
                    }
                }
                catch (Exception exUi)
                {
                    _logger.LogError(exUi, "Failed applying download queue refresh on UI thread");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh download queue");
        }
    }
    private async Task CheckPlatformHealthAsync() { try { PlatformHealth = await _apiService.GetAllPlatformHealthAsync(); OnPropertyChanged(nameof(PlatformHealth)); IsE621Enabled = IsE621Enabled && PlatformHealth.GetValueOrDefault("e621")?.IsAvailable == true; } catch (Exception ex) { _logger.LogError(ex, "Failed to check platform health"); } }
    private void OnDownloadStatusChanged(object? sender, DownloadJob job) { Dispatcher.UIThread.Post(() => { if (!string.IsNullOrEmpty(job.ParentId)) { return; } var existing = DownloadQueue.FirstOrDefault(j => j.Id == job.Id); if (existing != null) { existing.Status = job.Status; existing.DestinationPath = job.DestinationPath; existing.CompletedAt = job.CompletedAt; existing.ErrorMessage = job.ErrorMessage; existing.TotalBytes = job.TotalBytes; existing.BytesDownloaded = job.BytesDownloaded; } else { DownloadQueue.Add(job); } if (job.Status == DownloadStatus.Completed) { try { var match = SearchResults.FirstOrDefault(m => m.Id == job.MediaItem.Id); if (match != null) { OnPropertyChanged(nameof(SearchResults)); } } catch { } if (SelectedMedia?.Id == job.MediaItem.Id) { OnPropertyChanged(nameof(IsSelectedDownloaded)); } } }); }
    private void OnDownloadProgressUpdated(object? sender, DownloadJob job) { Dispatcher.UIThread.Post(() => { if (!string.IsNullOrEmpty(job.ParentId)) return; var existing = DownloadQueue.FirstOrDefault(j => j.Id == job.Id); if (existing != null) { existing.BytesDownloaded = job.BytesDownloaded; existing.TotalBytes = job.TotalBytes; } }); }
    [ObservableProperty] private bool _showFavoritesButton; private void UpdateFavoritesVisibility() { var user = _settingsService.GetSetting<string>("E621Username", "") ?? ""; var key = _settingsService.GetSetting<string>("E621ApiKey", "") ?? ""; ShowFavoritesButton = !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(key); }
    [RelayCommand] private async Task FavoritesAsync() { var user = _settingsService.GetSetting<string>("E621Username", "") ?? ""; if (string.IsNullOrWhiteSpace(user)) return; IncludeTags.Clear(); ExcludeTags.Clear(); IncludeTags.Add($"fav:{user}"); await SearchAsync(); }
    [RelayCommand] private async Task SaveSearchAsync() { var name = SaveSearchName?.Trim(); if (string.IsNullOrWhiteSpace(name)) return; var ss = new SavedSearch { Name = name, IncludeTags = IncludeTags.ToList(), ExcludeTags = ExcludeTags.ToList(), RatingFilterIndex = RatingFilterIndex }; var existing = SavedSearches.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); if (existing != null) SavedSearches.Remove(existing); SavedSearches.Add(ss); await PersistSavedSearchesAsync(); SaveSearchName = string.Empty; }
    [RelayCommand] private async Task DeleteSavedSearchAsync(SavedSearch? ss) { if (ss == null) return; SavedSearches.Remove(ss); await PersistSavedSearchesAsync(); }
    [RelayCommand] private async Task ApplySavedSearchAsync(SavedSearch? ss) { if (ss == null) return; IncludeTags.Clear(); foreach (var t in ss.IncludeTags) IncludeTags.Add(t); ExcludeTags.Clear(); foreach (var t in ss.ExcludeTags) ExcludeTags.Add(t); RatingFilterIndex = ss.RatingFilterIndex; await SearchAsync(); try { if (_settingsService.GetSetting<bool>("LoadLastSessionEnabled", true)) await PersistLastSessionAsync(); } catch { } }
    private async Task PersistSavedSearchesAsync() { try { var json = JsonSerializer.Serialize(SavedSearches.ToList()); await _settingsService.SetSettingAsync("SavedSearches", json); } catch { } }

    // Clears the current selection (bound to Esc)
    [RelayCommand]
    private void SelectNone() => SelectedMedia = null;

    // --- UI Helper Commands ---
    public bool CanOpenDownloadsFolder => true; // always enabled; path existence checked in command
    public bool CanOpenSelectedInBrowser => SelectedMedia != null && (!string.IsNullOrWhiteSpace(SelectedMedia.SourceUrl) || !string.IsNullOrWhiteSpace(SelectedMedia.FullImageUrl) || !string.IsNullOrWhiteSpace(SelectedMedia.PreviewUrl));
    public bool CanOpenViewer => SelectedMedia != null; // future: additional checks
    public bool CanOpenPoolPage => IsPoolMode && CurrentPoolId.HasValue;

    [RelayCommand]
    private void OpenDownloadsFolder()
    {
        try
        {
            var baseDir = _settingsService.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            if (!Directory.Exists(baseDir)) { try { Directory.CreateDirectory(baseDir); } catch { } }
            _shell?.OpenFolder(baseDir);
        }
        catch { }
    }

    [RelayCommand]
    private void OpenSelectedInBrowser()
    {
        try
        {
            var url = SelectedMedia?.SourceUrl;
            if (string.IsNullOrWhiteSpace(url)) url = SelectedMedia?.FullImageUrl;
            if (string.IsNullOrWhiteSpace(url)) url = SelectedMedia?.PreviewUrl;
            if (!string.IsNullOrWhiteSpace(url)) _shell?.OpenUrl(url);
        }
        catch { }
    }

    [RelayCommand]
    private void OpenViewer()
    {
        // Notify UI layer with navigation context (snapshot of current SearchResults)
        try
        {
            if (SelectedMedia != null)
            {
                var list = SearchResults.ToList();
                var idx = list.FindIndex(m => m.Id == SelectedMedia.Id);
                if (idx < 0) idx = 0;
                int? poolId = (IsPoolMode && CurrentPoolId.HasValue) ? CurrentPoolId : null;
                WeakReferenceMessenger.Default.Send(new OpenViewerRequestMessage(new OpenViewerRequest(list, idx, poolId)));
            }
        }
        catch { }
    }

    [RelayCommand(CanExecute = nameof(CanOpenPoolPage))]
    private void OpenPoolPage()
    {
        try
        {
            if (!CanOpenPoolPage) return;
            var id = CurrentPoolId!.Value;
            // e621 pool URL format
            var url = $"https://e621.net/pools/{id}";
            _shell?.OpenUrl(url);
        }
        catch { }
    }

    // Called by view when pool selection changes (e.g., SelectionChanged event) to auto-load pool
    public async Task OnPoolSelectionChangedAsync()
    {
        try { if (SelectedPool != null) await LoadSelectedPoolAsync(SelectedPool); } catch { }
    }
}
