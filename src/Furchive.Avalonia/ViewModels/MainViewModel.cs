using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Avalonia.Threading;
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

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isE621Enabled = true;
    [ObservableProperty] private bool _isSearching = false;
    [ObservableProperty] private MediaItem? _selectedMedia;
    partial void OnSelectedMediaChanged(MediaItem? value) { OnPropertyChanged(nameof(IsSelectedDownloaded)); _ = EnsurePreviewPoolInfoAsync(value); }
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private bool _isBackgroundCaching = false;
    [ObservableProperty] private int _backgroundCachingCurrent = 0;
    [ObservableProperty] private int _backgroundCachingTotal = 0;
    public int BackgroundCachingPercent => BackgroundCachingTotal <= 0 ? 0 : (int)Math.Round(100.0 * BackgroundCachingCurrent / (double)BackgroundCachingTotal);
    [ObservableProperty] private int _backgroundCachingItemsFetched = 0;
    [ObservableProperty] private int _ratingFilterIndex = 0;

    public ObservableCollection<MediaItem> SearchResults { get; } = new();
    public ObservableCollection<string> IncludeTags { get; } = new();
    public ObservableCollection<string> ExcludeTags { get; } = new();
    public ObservableCollection<ContentRating> SelectedRatings { get; } = new() { ContentRating.Safe };
    public ObservableCollection<DownloadJob> DownloadQueue { get; } = new();

    [ObservableProperty] private string _poolSearch = string.Empty;
    [ObservableProperty] private PoolInfo? _selectedPool;
    partial void OnSelectedPoolChanged(PoolInfo? value) { OnPropertyChanged(nameof(ShowPinPoolButton)); }
    [ObservableProperty] private bool _isPoolMode = false;
    partial void OnIsPoolModeChanged(bool value) { OnPropertyChanged(nameof(DownloadAllLabel)); OnPropertyChanged(nameof(ShowPinPoolButton)); }
    [ObservableProperty] private int? _currentPoolId = null;
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

    public partial class SavedSearch { public string Name { get; set; } = string.Empty; public List<string> IncludeTags { get; set; } = new(); public List<string> ExcludeTags { get; set; } = new(); public int RatingFilterIndex { get; set; } public string? SearchQuery { get; set; } }
    [ObservableProperty] private string _saveSearchName = string.Empty;
    public ObservableCollection<SavedSearch> SavedSearches { get; } = new();
    public Dictionary<string, PlatformHealth> PlatformHealth { get; private set; } = new();
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private bool _hasNextPage = false;
    [ObservableProperty] private int _totalCount = 0;
    public bool CanGoPrev => CurrentPage > 1;
    public bool CanGoNext => HasNextPage;
    public string PageInfo => $"Page {CurrentPage}{(TotalCount > 0 ? $" • {TotalCount} total" : string.Empty)}";

    // Gallery sizing
    public double GalleryTileWidth => 180 * GalleryScale;
    public double GalleryTileHeight => 210 * GalleryScale;
    public double GalleryImageSize => 170 * GalleryScale;
    private double _galleryScale = 1.0;
    public double GalleryScale { get => _galleryScale; set { if (Math.Abs(_galleryScale - value) > 0.0001) { _galleryScale = value; OnPropertyChanged(nameof(GalleryScale)); OnPropertyChanged(nameof(GalleryTileWidth)); OnPropertyChanged(nameof(GalleryTileHeight)); OnPropertyChanged(nameof(GalleryImageSize)); } } }

    public MainViewModel(IUnifiedApiService apiService, IDownloadService downloadService, ISettingsService settingsService, ILogger<MainViewModel> logger, IEnumerable<IPlatformApi> platformApis, IThumbnailCacheService thumbCache, ICpuWorkQueue cpuQueue, IPoolsCacheStore cacheStore)
    {
        _apiService = apiService; _downloadService = downloadService; _settingsService = settingsService; _logger = logger; _thumbCache = thumbCache; _cpuQueue = cpuQueue; _cacheStore = cacheStore;
        foreach (var p in platformApis) { _apiService.RegisterPlatform(p); if (string.Equals(p.PlatformName, "e621", StringComparison.OrdinalIgnoreCase)) { _e621Platform = p; } }
        _downloadService.DownloadStatusChanged += OnDownloadStatusChanged; _downloadService.DownloadProgressUpdated += OnDownloadProgressUpdated;
    LoadSettings();
        try { var jsonPinned = _settingsService.GetSetting<string>("PinnedPools", "[]") ?? "[]"; var pinned = JsonSerializer.Deserialize<List<PoolInfo>>(jsonPinned) ?? new(); PinnedPools.Clear(); foreach (var p in pinned) PinnedPools.Add(p); } catch { }
        GalleryScale = Math.Clamp(_settingsService.GetSetting<double>("GalleryScale", 1.0), 0.75, 1.5);
        UpdateFavoritesVisibility();
        _ = Task.Run(() => AuthenticatePlatformsAsync(platformApis));
        _ = Task.Run(CheckPlatformHealthAsync);
        // Pools: load from SQLite cache on startup; refresh only if empty
        _ = Task.Run(async () =>
        {
            try { await _cacheStore.InitializeAsync(); } catch { }
            bool hadCached = false;
            try { hadCached = await LoadPoolsFromDbAsync(); }
            catch { }
            try
            {
                if (!hadCached)
                {
                    // No cache available -> do initial full fetch once
                    await RefreshPoolsIfStaleAsync();
                }
                else
                {
                    // Cache exists -> only do a small incremental update in background
                    _ = IncrementalUpdatePoolsAsync(TimeSpan.FromMinutes(5));
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Pools refresh/incremental on startup failed"); }
        });
        _ = RestoreLastSessionAsync();
    WeakReferenceMessenger.Default.Register<PoolsCacheRebuiltMessage>(this, async (_, __) => { try { _ = await LoadPoolsFromDbAsync(); Dispatcher.UIThread.Post(() => ApplyPoolsFilter()); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to update pools after cache rebuild notification"); } });
        WeakReferenceMessenger.Default.Register<PoolsCacheRebuildRequestedMessage>(this, async (_, __) => { try { Dispatcher.UIThread.Post(() => { Pools.Clear(); FilteredPools.Clear(); PoolsStatusText = "rebuilding cache…"; }); var file = GetPoolsCacheFilePath(); try { if (File.Exists(file)) File.Delete(file); } catch { } _poolsCacheLastSavedUtc = DateTime.MinValue; await RefreshPoolsIfStaleAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to rebuild pools cache on request"); } });
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (_, __) => { try { UpdateFavoritesVisibility(); GalleryScale = Math.Clamp(_settingsService.GetSetting<double>("GalleryScale", GalleryScale), 0.75, 1.5); } catch { } });
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
    }

    [RelayCommand] private async Task NextPageAsync() { if (!CanGoNext || IsSearching) return; try { if (IsPoolMode && CurrentPoolId.HasValue) { await PerformPoolPageAsync(CurrentPoolId.Value, CurrentPage + 1, reset: true); } else { await PerformSearchAsync(CurrentPage + 1, reset: true); } } catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; _logger.LogError(ex, "Next page failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Next page failed", ex.Message)); } catch { } } }
    [RelayCommand] private async Task PrevPageAsync() { if (!CanGoPrev || IsSearching) return; try { if (IsPoolMode && CurrentPoolId.HasValue) { await PerformPoolPageAsync(CurrentPoolId.Value, CurrentPage - 1, reset: true); } else { await PerformSearchAsync(CurrentPage - 1, reset: true); } } catch (Exception ex) { StatusMessage = $"Search failed: {ex.Message}"; _logger.LogError(ex, "Prev page failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Prev page failed", ex.Message)); } catch { } } }

    private async Task PerformSearchAsync(int page, bool reset)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) { IsSearching = true; StatusMessage = "Searching..."; if (reset) SearchResults.Clear(); }
            else { await Dispatcher.UIThread.InvokeAsync(() => { IsSearching = true; StatusMessage = "Searching..."; if (reset) SearchResults.Clear(); }); }

            await EnsureE621AuthAsync();
            var sources = new List<string>(); if (IsE621Enabled) sources.Add("e621"); if (!sources.Any()) sources.Add("e621");
            var (inlineInclude, inlineExclude) = ParseQuery(SearchQuery); var includeTags = IncludeTags.Union(inlineInclude, StringComparer.OrdinalIgnoreCase).ToList(); var excludeTags = ExcludeTags.Union(inlineExclude, StringComparer.OrdinalIgnoreCase).ToList();
            var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } };
            var searchParams = new SearchParameters { IncludeTags = includeTags, ExcludeTags = excludeTags, Sources = sources, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = page, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) };
            var result = await _apiService.SearchAsync(searchParams);
            if (Dispatcher.UIThread.CheckAccess()) { foreach (var item in result.Items) { if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) { item.PreviewUrl = item.FullImageUrl; } SearchResults.Add(item); } CurrentPage = page; HasNextPage = result.HasNextPage; TotalCount = result.TotalCount; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo)); }
            else { await Dispatcher.UIThread.InvokeAsync(() => { foreach (var item in result.Items) { if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) { item.PreviewUrl = item.FullImageUrl; } SearchResults.Add(item); } CurrentPage = page; HasNextPage = result.HasNextPage; TotalCount = result.TotalCount; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo)); }); }
            var status = result.Errors.Any() ? $"Found {result.Items.Count} items with errors: {string.Join(", ", result.Errors.Keys)}" : $"Found {result.Items.Count} items"; if (Dispatcher.UIThread.CheckAccess()) StatusMessage = status; else await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = status);
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
            var (inlineInclude, inlineExclude) = ParseQuery(SearchQuery);
            var includeTags = IncludeTags.Union(inlineInclude, StringComparer.OrdinalIgnoreCase).ToList();
            var excludeTags = ExcludeTags.Union(inlineExclude, StringComparer.OrdinalIgnoreCase).ToList();
            var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } };
            var nextPage = CurrentPage + 1;
            var searchParams = new SearchParameters { IncludeTags = includeTags, ExcludeTags = excludeTags, Sources = sources, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = nextPage, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) };
            var result = await _apiService.SearchAsync(searchParams);
            foreach (var item in result.Items)
            {
                if (string.IsNullOrWhiteSpace(item.PreviewUrl) && !string.IsNullOrWhiteSpace(item.FullImageUrl)) item.PreviewUrl = item.FullImageUrl;
                SearchResults.Add(item);
            }
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

    private async Task PrefetchNextPagesAsync(int currentPage)
    {
        try
        {
            var ahead = Math.Clamp(_settingsService.GetSetting<int>("E621SearchPrefetchPagesAhead", 2), 0, 5); if (ahead <= 0) return;
            await EnsureE621AuthAsync(); var (inlineInclude, inlineExclude) = ParseQuery(SearchQuery); var includeTags = IncludeTags.Union(inlineInclude, StringComparer.OrdinalIgnoreCase).ToList(); var excludeTags = ExcludeTags.Union(inlineExclude, StringComparer.OrdinalIgnoreCase).ToList(); var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } }; var limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50);
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

    [RelayCommand] private async Task PinSelectedPoolAsync() { try { if (!IsPoolMode) return; var pool = SelectedPool; if (pool == null) return; if (!PinnedPools.Any(p => p.Id == pool.Id)) { PinnedPools.Add(new PoolInfo { Id = pool.Id, Name = pool.Name, PostCount = pool.PostCount }); await PersistPinnedPoolsAsync(); } } catch { } }
    [RelayCommand] private async Task UnpinPoolAsync(PoolInfo? pool) { if (pool == null) return; try { var existing = PinnedPools.FirstOrDefault(p => p.Id == pool.Id); if (existing != null) PinnedPools.Remove(existing); await PersistPinnedPoolsAsync(); } catch { } }
    private async Task PersistPinnedPoolsAsync() { try { var json = JsonSerializer.Serialize(PinnedPools.ToList()); await _settingsService.SetSettingAsync("PinnedPools", json); } catch { } }
    private string GetPoolsCacheFilePath() => Path.Combine(_cacheDir, "e621_pools.json");

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
            var file = GetPoolsCacheFilePath();
            if (File.Exists(file) && Pools.Any()) { return; }
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
            Dispatcher.UIThread.Post(() => { Pools.Clear(); foreach (var p in list) Pools.Add(p); ApplyPoolsFilter(); PoolsStatusText = $"{Pools.Count} pools"; });
            try { await _cacheStore.UpsertPoolsAsync(Pools.ToList(), CancellationToken.None); var saved = await _cacheStore.GetPoolsSavedAtAsync(); _poolsCacheLastSavedUtc = saved ?? DateTime.UtcNow; } catch { }
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
            Dispatcher.UIThread.Post(() =>
            {
                var map = Pools.ToDictionary(p => p.Id);
                foreach (var u in updates)
                {
                    if (!u.Name.StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) && u.PostCount > 0) { map[u.Id] = u; }
                    else { map.Remove(u.Id); }
                }
                Pools.Clear();
                foreach (var p in map.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) Pools.Add(p);
                ApplyPoolsFilter();
                PoolsStatusText = $"{Pools.Count} pools";
            });
            try { await _cacheStore.UpsertPoolsAsync(Pools.ToList(), CancellationToken.None); var saved = await _cacheStore.GetPoolsSavedAtAsync(); _poolsCacheLastSavedUtc = saved ?? DateTime.UtcNow; } catch { }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Incremental pool update failed"); }
        finally { /* no auto-reschedule; run only when explicitly requested */ }
    }

    private async Task StartOrKickIncrementalAsync() { try { var file = GetPoolsCacheFilePath(); if (!File.Exists(file) || !Pools.Any()) { await RefreshPoolsIfStaleAsync(); return; } } catch { } /* Startup no longer auto-runs incremental. This method retained for compatibility if invoked elsewhere. */ }
    [RelayCommand] private void CancelPoolsUpdate() { try { _poolsCts?.Cancel(); } catch { } }
    [RelayCommand] private void RunPoolsFilter() => ApplyPoolsFilter();
    [RelayCommand] private async Task LoadSelectedPoolAsync(PoolInfo? fromPinned = null)
    {
        var pool = fromPinned ?? SelectedPool; if (pool == null || IsSearching) return;
        try
        {
            IsSearching = true; StatusMessage = $"Loading pool {pool.Id} ({pool.Name})..."; SearchResults.Clear(); CurrentPage = 1;
            await EnsureE621AuthAsync(); IsPoolMode = true; CurrentPoolId = pool.Id; SelectedPool = Pools.FirstOrDefault(p => p.Id == pool.Id) ?? pool;

            // 1) Try cached posts first
            var cached = await _cacheStore.GetPoolPostsAsync(pool.Id);
            if (cached != null && cached.Count > 0)
            {
                var poolName = pool.Name;
                for (int i = 0; i < cached.Count; i++)
                {
                    var pageNum = (i + 1).ToString("D5");
                    cached[i].TagCategories ??= new Dictionary<string, List<string>>();
                    cached[i].TagCategories["pool_name"] = new List<string> { poolName };
                    cached[i].TagCategories["page_number"] = new List<string> { pageNum };
                    if (string.IsNullOrWhiteSpace(cached[i].PreviewUrl) && !string.IsNullOrWhiteSpace(cached[i].FullImageUrl)) cached[i].PreviewUrl = cached[i].FullImageUrl;
                    SearchResults.Add(cached[i]);
                }
                HasNextPage = false; TotalCount = cached.Count; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo));
                StatusMessage = $"Loaded cached pool {pool.Id}: {cached.Count} items (checking for updates…)";
            }

            // 2) In background, fetch latest posts and update DB; if changed, reload UI
            _ = Task.Run(async () =>
            {
                try
                {
                    var fresh = await _apiService.GetAllPoolPostsAsync("e621", pool.Id);
                    if (fresh == null) fresh = new List<MediaItem>();
                    // Compare counts/IDs to detect change
                    bool changed = false;
                    if (cached == null || cached.Count != fresh.Count) changed = true;
                    else if (cached.Zip(fresh, (a, b) => a.Id == b.Id).Any(eq => !eq)) changed = true;

                    if (changed)
                    {
                        await _cacheStore.UpsertPoolPostsAsync(pool.Id, fresh);
                        // Rebind on UI thread if user still viewing this pool
                        if (CurrentPoolId == pool.Id)
                        {
                            var poolName = pool.Name;
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                SearchResults.Clear();
                                for (int i = 0; i < fresh.Count; i++)
                                {
                                    var pageNum = (i + 1).ToString("D5");
                                    fresh[i].TagCategories ??= new Dictionary<string, List<string>>();
                                    fresh[i].TagCategories["pool_name"] = new List<string> { poolName };
                                    fresh[i].TagCategories["page_number"] = new List<string> { pageNum };
                                    if (string.IsNullOrWhiteSpace(fresh[i].PreviewUrl) && !string.IsNullOrWhiteSpace(fresh[i].FullImageUrl)) fresh[i].PreviewUrl = fresh[i].FullImageUrl;
                                    SearchResults.Add(fresh[i]);
                                }
                                HasNextPage = false; TotalCount = fresh.Count; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo));
                                StatusMessage = $"Updated pool {pool.Id}: {SearchResults.Count} items";
                            });
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Background refresh of pool {PoolId} failed", pool.Id); }
            });

            // If no cached items were available, do an immediate fetch for initial display
            if (SearchResults.Count == 0)
            {
                var items = await _apiService.GetAllPoolPostsAsync("e621", pool.Id);
                if (items == null || items.Count == 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _excludedPoolIds.Add(pool.Id);
                        var toRemove = Pools.FirstOrDefault(p => p.Id == pool.Id);
                        if (toRemove != null) Pools.Remove(toRemove);
                        ApplyPoolsFilter();
                        PoolsStatusText = $"{Pools.Count} pools";
                    });
                    StatusMessage = "Pool appears empty or unavailable.";
                    return;
                }
                var poolName = pool.Name;
                for (int i = 0; i < items.Count; i++)
                {
                    var pageNum = (i + 1).ToString("D5");
                    items[i].TagCategories ??= new Dictionary<string, List<string>>();
                    items[i].TagCategories["pool_name"] = new List<string> { poolName };
                    items[i].TagCategories["page_number"] = new List<string> { pageNum };
                    if (string.IsNullOrWhiteSpace(items[i].PreviewUrl) && !string.IsNullOrWhiteSpace(items[i].FullImageUrl)) items[i].PreviewUrl = items[i].FullImageUrl;
                    SearchResults.Add(items[i]);
                }
                HasNextPage = false; TotalCount = items.Count; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo));
                StatusMessage = $"Loaded pool {pool.Id}: {SearchResults.Count} items";
                try { await _cacheStore.UpsertPoolPostsAsync(pool.Id, items); } catch { }
            }

            try { await PersistLastSessionAsync(); } catch { }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load pool: {ex.Message}"; _logger.LogError(ex, "Pool load failed");
            try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Load pool failed", ex.Message)); } catch { }
        }
        finally { IsSearching = false; }
    }

    public Task TriggerLoadSelectedPoolAsync() => LoadSelectedPoolAsync(null);
    private async Task PerformPoolPageAsync(int poolId, int page, bool reset) { await Task.CompletedTask; }
    [RelayCommand] private async Task SoftRefreshPoolsAsync() { try { var minutes = Math.Max(5, _settingsService.GetSetting<int>("PoolsUpdateIntervalMinutes", 360)); await IncrementalUpdatePoolsAsync(TimeSpan.FromMinutes(minutes)); } catch (Exception ex) { _logger.LogWarning(ex, "Soft refresh pools failed"); } }
    // JSON cache replaced by SQLite-backed store
    private async Task PersistLastSessionAsync() { try { var session = new LastSession { IsPoolMode = IsPoolMode, PoolId = CurrentPoolId, SearchQuery = SearchQuery, Include = IncludeTags.ToList(), Exclude = ExcludeTags.ToList(), RatingFilterIndex = RatingFilterIndex, Page = CurrentPage }; var json = JsonSerializer.Serialize(session); await _settingsService.SetSettingAsync("LastSession", json); } catch { } }
    private async Task RestoreLastSessionAsync() { try { var json = _settingsService.GetSetting<string>("LastSession", null); if (string.IsNullOrWhiteSpace(json)) return; var session = JsonSerializer.Deserialize<LastSession>(json); if (session == null) return; RatingFilterIndex = session.RatingFilterIndex; SearchQuery = session.SearchQuery ?? string.Empty; IncludeTags.Clear(); foreach (var t in (session.Include ?? new())) IncludeTags.Add(t); ExcludeTags.Clear(); foreach (var t in (session.Exclude ?? new())) ExcludeTags.Add(t); if (session.IsPoolMode && session.PoolId.HasValue) { CurrentPoolId = session.PoolId; IsPoolMode = true; SelectedPool = Pools.FirstOrDefault(p => p.Id == session.PoolId.Value); await EnsureE621AuthAsync(); var items = await _apiService.GetAllPoolPostsAsync("e621", session.PoolId.Value); if (items != null && items.Count > 0) { SearchResults.Clear(); var poolName = SelectedPool?.Name ?? (items.FirstOrDefault()?.TagCategories?.GetValueOrDefault("pool_name")?.FirstOrDefault() ?? ""); for (int i = 0; i < items.Count; i++) { var pageNum = (i + 1).ToString("D5"); items[i].TagCategories ??= new Dictionary<string, List<string>>(); items[i].TagCategories["pool_name"] = new List<string> { poolName }; items[i].TagCategories["page_number"] = new List<string> { pageNum }; if (string.IsNullOrWhiteSpace(items[i].PreviewUrl) && !string.IsNullOrWhiteSpace(items[i].FullImageUrl)) items[i].PreviewUrl = items[i].FullImageUrl; SearchResults.Add(items[i]); } CurrentPage = 1; HasNextPage = false; TotalCount = items.Count; OnPropertyChanged(nameof(CanGoPrev)); OnPropertyChanged(nameof(CanGoNext)); OnPropertyChanged(nameof(PageInfo)); StatusMessage = $"Restored last pool: {session.PoolId}"; return; } } await PerformSearchAsync(Math.Max(1, session.Page), reset: true); } catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore last session"); } }
    private sealed class LastSession { public bool IsPoolMode { get; set; } public int? PoolId { get; set; } public string? SearchQuery { get; set; } public List<string> Include { get; set; } = new(); public List<string> Exclude { get; set; } = new(); public int RatingFilterIndex { get; set; } public int Page { get; set; } = 1; }
    public static (IEnumerable<string> include, IEnumerable<string> exclude) ParseQuery(string? query) { if (string.IsNullOrWhiteSpace(query)) return (Enumerable.Empty<string>(), Enumerable.Empty<string>()); var parts = query.Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries); var include = new List<string>(); var exclude = new List<string>(); foreach (var raw in parts) { var t = raw.Trim(); if (t.StartsWith("-")) { t = t.Substring(1); if (!string.IsNullOrWhiteSpace(t)) exclude.Add(t); } else include.Add(t); } return (include, exclude); }
    public async Task<MediaItem?> FetchNextFromApiAsync(bool forward) { var ratings = RatingFilterIndex switch { 0 => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit }, 1 => new List<ContentRating> { ContentRating.Explicit }, 2 => new List<ContentRating> { ContentRating.Questionable }, 3 => new List<ContentRating> { ContentRating.Safe }, _ => new List<ContentRating> { ContentRating.Safe, ContentRating.Questionable, ContentRating.Explicit } }; var (inc, exc) = ParseQuery(SearchQuery); var include = IncludeTags.Union(inc, StringComparer.OrdinalIgnoreCase).ToList(); var exclude = ExcludeTags.Union(exc, StringComparer.OrdinalIgnoreCase).ToList(); var page = Math.Max(1, CurrentPage + (forward ? 1 : -1)); var result = await _apiService.SearchAsync(new SearchParameters { IncludeTags = include, ExcludeTags = exclude, Sources = new List<string> { "e621" }, Ratings = ratings, Sort = Furchive.Core.Models.SortOrder.Newest, Page = page, Limit = _settingsService.GetSetting<int>("MaxResultsPerSource", 50) }); return result.Items.FirstOrDefault(); }
    [RelayCommand] private void AddIncludeTag(string tag) { if (!string.IsNullOrWhiteSpace(tag) && !IncludeTags.Contains(tag)) { IncludeTags.Add(tag); } }
    [RelayCommand] private void RemoveIncludeTag(string tag) { IncludeTags.Remove(tag); }
    [RelayCommand] private void AddExcludeTag(string tag) { if (!string.IsNullOrWhiteSpace(tag) && !ExcludeTags.Contains(tag)) { ExcludeTags.Add(tag); } }
    [RelayCommand] private void RemoveExcludeTag(string tag) { ExcludeTags.Remove(tag); }
    [RelayCommand] private async Task DownloadSelectedAsync() { if (SelectedMedia == null) return; try { var downloadPath = _settingsService.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); var tempPath = GetTempPathFor(SelectedMedia); var finalPath = GenerateFinalPath(SelectedMedia, downloadPath); Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!); if (File.Exists(tempPath) && !File.Exists(finalPath)) { File.Move(tempPath, finalPath); StatusMessage = $"Saved from temp: {SelectedMedia.Title}"; OnPropertyChanged(nameof(IsSelectedDownloaded)); return; } await _downloadService.QueueDownloadAsync(SelectedMedia, downloadPath); StatusMessage = $"Queued download: {SelectedMedia.Title}"; } catch (Exception ex) { StatusMessage = $"Download failed: {ex.Message}"; _logger.LogError(ex, "Download failed for {Title}", SelectedMedia.Title); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Download failed", ex.Message)); } catch { } } }
    [RelayCommand] private async Task DownloadAllAsync() { if (!SearchResults.Any()) return; try { var downloadPath = _settingsService.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); var toQueue = new List<MediaItem>(); foreach (var m in SearchResults) { var tempPath = GetTempPathFor(m); var finalPath = GenerateFinalPath(m, downloadPath); Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!); if (File.Exists(tempPath) && !File.Exists(finalPath)) { try { File.Move(tempPath, finalPath); } catch { toQueue.Add(m); } } else { toQueue.Add(m); } } if (toQueue.Any()) { if (IsPoolMode && CurrentPoolId.HasValue) { var label = SelectedPool?.Name ?? PreviewPoolName ?? "Pool"; await _downloadService.QueueAggregateDownloadsAsync("pool", toQueue, downloadPath, label); } else { await _downloadService.QueueMultipleDownloadsAsync(toQueue, downloadPath); } } StatusMessage = IsPoolMode ? $"Queued pool downloads ({SearchResults.Count} items)" : $"Queued {SearchResults.Count} downloads"; } catch (Exception ex) { StatusMessage = $"Batch download failed: {ex.Message}"; _logger.LogError(ex, "Batch download failed"); try { WeakReferenceMessenger.Default.Send(new UiErrorMessage("Batch download failed", ex.Message)); } catch { } } }

    // Queue control commands (optional for bindings)
    [RelayCommand] private async Task PauseJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.PauseDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Pause failed"); } }
    [RelayCommand] private async Task ResumeJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.ResumeDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Resume failed"); } }
    [RelayCommand] private async Task CancelJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.CancelDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Cancel failed"); } }
    [RelayCommand] private async Task RetryJobAsync(DownloadJob? job) { if (job == null) return; try { await _downloadService.RetryDownloadAsync(job.Id); } catch (Exception ex) { _logger.LogWarning(ex, "Retry failed"); } }
    private static string GetTempPathFor(MediaItem item) { var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp"); var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension; var safeArtist = new string((item.Artist ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Replace(" ", "_"); var safeTitle = new string((item.Title ?? string.Empty).Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Replace(" ", "_"); var file = $"{item.Source}_{item.Id}_{safeArtist}_{safeTitle}.{ext}"; return Path.Combine(tempDir, file); }
    private string GenerateFinalPath(MediaItem mediaItem, string basePath) { var hasPoolContext = mediaItem.TagCategories != null && (mediaItem.TagCategories.ContainsKey("page_number") || mediaItem.TagCategories.ContainsKey("pool_name")); var template = hasPoolContext ? (_settingsService.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") : (_settingsService.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}"); var extFinal = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? TryGetExtensionFromUrl(mediaItem.FullImageUrl) ?? "bin" : mediaItem.FileExtension; string Sanitize(string s) { var invalid = Path.GetInvalidFileNameChars(); var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()); return clean.Replace(" ", "_"); } var filenameRel = template.Replace("{source}", mediaItem.Source).Replace("{artist}", Sanitize(mediaItem.Artist)).Replace("{id}", mediaItem.Id).Replace("{safeTitle}", Sanitize(mediaItem.Title)).Replace("{ext}", extFinal).Replace("{pool_name}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : (SelectedPool?.Name ?? string.Empty))).Replace("{page_number}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty)); return Path.Combine(basePath, filenameRel); }
    [RelayCommand] private async Task RefreshDownloadQueueAsync() { try { var jobs = await _downloadService.GetDownloadJobsAsync(); jobs = jobs.Where(j => string.IsNullOrEmpty(j.ParentId)).ToList(); var map = DownloadQueue.ToDictionary(j => j.Id); var incomingIds = new HashSet<string>(jobs.Select(j => j.Id)); foreach (var job in jobs) { if (map.TryGetValue(job.Id, out var existing)) { existing.Status = job.Status; existing.DestinationPath = job.DestinationPath; existing.CompletedAt = job.CompletedAt; existing.ErrorMessage = job.ErrorMessage; existing.TotalBytes = job.TotalBytes; existing.BytesDownloaded = job.BytesDownloaded; } else { DownloadQueue.Add(job); } } for (int i = DownloadQueue.Count - 1; i >= 0; i--) { if (!incomingIds.Contains(DownloadQueue[i].Id)) DownloadQueue.RemoveAt(i); } } catch (Exception ex) { _logger.LogError(ex, "Failed to refresh download queue"); } }
    private async Task CheckPlatformHealthAsync() { try { PlatformHealth = await _apiService.GetAllPlatformHealthAsync(); OnPropertyChanged(nameof(PlatformHealth)); IsE621Enabled = IsE621Enabled && PlatformHealth.GetValueOrDefault("e621")?.IsAvailable == true; } catch (Exception ex) { _logger.LogError(ex, "Failed to check platform health"); } }
    private void OnDownloadStatusChanged(object? sender, DownloadJob job) { Dispatcher.UIThread.Post(() => { if (!string.IsNullOrEmpty(job.ParentId)) { return; } var existing = DownloadQueue.FirstOrDefault(j => j.Id == job.Id); if (existing != null) { existing.Status = job.Status; existing.DestinationPath = job.DestinationPath; existing.CompletedAt = job.CompletedAt; existing.ErrorMessage = job.ErrorMessage; existing.TotalBytes = job.TotalBytes; existing.BytesDownloaded = job.BytesDownloaded; } else { DownloadQueue.Add(job); } if (job.Status == DownloadStatus.Completed) { try { var match = SearchResults.FirstOrDefault(m => m.Id == job.MediaItem.Id); if (match != null) { OnPropertyChanged(nameof(SearchResults)); } } catch { } if (SelectedMedia?.Id == job.MediaItem.Id) { OnPropertyChanged(nameof(IsSelectedDownloaded)); } } }); }
    private void OnDownloadProgressUpdated(object? sender, DownloadJob job) { Dispatcher.UIThread.Post(() => { if (!string.IsNullOrEmpty(job.ParentId)) return; var existing = DownloadQueue.FirstOrDefault(j => j.Id == job.Id); if (existing != null) { existing.BytesDownloaded = job.BytesDownloaded; existing.TotalBytes = job.TotalBytes; } }); }
    [ObservableProperty] private bool _showFavoritesButton; private void UpdateFavoritesVisibility() { var user = _settingsService.GetSetting<string>("E621Username", "") ?? ""; var key = _settingsService.GetSetting<string>("E621ApiKey", "") ?? ""; ShowFavoritesButton = !string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(key); }
    [RelayCommand] private async Task FavoritesAsync() { var user = _settingsService.GetSetting<string>("E621Username", "") ?? ""; if (string.IsNullOrWhiteSpace(user)) return; SearchQuery = $"fav:{user}"; IncludeTags.Clear(); ExcludeTags.Clear(); await SearchAsync(); }
    [RelayCommand] private async Task SaveSearchAsync() { var name = SaveSearchName?.Trim(); if (string.IsNullOrWhiteSpace(name)) return; var ss = new SavedSearch { Name = name, IncludeTags = IncludeTags.ToList(), ExcludeTags = ExcludeTags.ToList(), RatingFilterIndex = RatingFilterIndex, SearchQuery = SearchQuery }; var existing = SavedSearches.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)); if (existing != null) SavedSearches.Remove(existing); SavedSearches.Add(ss); await PersistSavedSearchesAsync(); SaveSearchName = string.Empty; }
    [RelayCommand] private async Task DeleteSavedSearchAsync(SavedSearch? ss) { if (ss == null) return; SavedSearches.Remove(ss); await PersistSavedSearchesAsync(); }
    [RelayCommand] private async Task ApplySavedSearchAsync(SavedSearch? ss) { if (ss == null) return; IncludeTags.Clear(); foreach (var t in ss.IncludeTags) IncludeTags.Add(t); ExcludeTags.Clear(); foreach (var t in ss.ExcludeTags) ExcludeTags.Add(t); RatingFilterIndex = ss.RatingFilterIndex; SearchQuery = ss.SearchQuery ?? string.Empty; await SearchAsync(); }
    private async Task PersistSavedSearchesAsync() { try { var json = JsonSerializer.Serialize(SavedSearches.ToList()); await _settingsService.SetSettingAsync("SavedSearches", json); } catch { } }

    // Clears the current selection (bound to Esc)
    [RelayCommand]
    private void SelectNone() => SelectedMedia = null;
}
