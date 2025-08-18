using Furchive.Core.Models;

namespace Furchive.Core.Interfaces;

/// <summary>
/// Interface for platform-specific API implementations
/// </summary>
public interface IPlatformApi
{
    /// <summary>
    /// Platform identifier (e621, furaffinity, inkbunny, weasyl)
    /// </summary>
    string PlatformName { get; }

    /// <summary>
    /// Check if the platform is available and authenticated
    /// </summary>
    Task<PlatformHealth> GetHealthAsync();

    /// <summary>
    /// Authenticate with the platform using provided credentials
    /// </summary>
    Task<bool> AuthenticateAsync(Dictionary<string, string> credentials);

    /// <summary>
    /// Search for media items
    /// </summary>
    Task<SearchResult> SearchAsync(SearchParameters parameters);

    /// <summary>
    /// Get detailed information about a specific media item
    /// </summary>
    Task<MediaItem?> GetMediaDetailsAsync(string id);

    /// <summary>
    /// Get tag suggestions for autocomplete
    /// </summary>
    Task<List<TagSuggestion>> GetTagSuggestionsAsync(string query, int limit = 10);

    /// <summary>
    /// Get the direct download URL for a media item
    /// </summary>
    Task<string?> GetDownloadUrlAsync(string id);

    /// <summary>
    /// Get list of pools available on this platform (if supported).
    /// </summary>
    Task<List<PoolInfo>> GetPoolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get list of pools with progress updates (current, total?).
    /// </summary>
    Task<List<PoolInfo>> GetPoolsAsync(IProgress<(int current, int? total)>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pools that have changed since the given UTC timestamp (best-effort).
    /// Implementations may fetch the most recently updated pages and stop when older than 'sinceUtc'.
    /// </summary>
    Task<List<PoolInfo>> GetPoolsUpdatedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get posts in a specific pool, ordered by pool order.
    /// </summary>
    Task<SearchResult> GetPoolPostsAsync(int poolId, int page = 1, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all posts in a pool with the original pool order.
    /// Ignores page/limit and returns the whole set.
    /// </summary>
    Task<List<MediaItem>> GetAllPoolPostsAsync(int poolId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Try to get pool context (pool id, name, and page number within the pool) for a given post id.
    /// Returns null if the post does not belong to any pool or the platform doesn't support it.
    /// </summary>
    Task<(int poolId, string poolName, int pageNumber)?> GetPoolContextForPostAsync(string postId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Unified API service that aggregates multiple platforms
/// </summary>
public interface IUnifiedApiService
{
    /// <summary>
    /// Get health status for all registered platforms
    /// </summary>
    Task<Dictionary<string, PlatformHealth>> GetAllPlatformHealthAsync();

    /// <summary>
    /// Search across multiple platforms
    /// </summary>
    Task<SearchResult> SearchAsync(SearchParameters parameters);

    /// <summary>
    /// Get media details from a specific platform
    /// </summary>
    Task<MediaItem?> GetMediaDetailsAsync(string source, string id);

    /// <summary>
    /// Get tag suggestions from enabled platforms
    /// </summary>
    Task<List<TagSuggestion>> GetTagSuggestionsAsync(string query, List<string> sources, int limit = 10);

    /// <summary>
    /// Register a platform API implementation
    /// </summary>
    void RegisterPlatform(IPlatformApi platformApi);

    /// <summary>
    /// Get pools from a specific source (e.g., e621) with optional caching handled by caller or service.
    /// </summary>
    Task<List<PoolInfo>> GetPoolsAsync(string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pools from a specific source with progress updates (current, total?).
    /// </summary>
    Task<List<PoolInfo>> GetPoolsAsync(string source, IProgress<(int current, int? total)>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get posts in a pool from a specific source.
    /// </summary>
    Task<SearchResult> GetPoolPostsAsync(string source, int poolId, int page = 1, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pools updated since the given UTC timestamp from a specific source.
    /// </summary>
    Task<List<PoolInfo>> GetPoolsUpdatedSinceAsync(string source, DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all posts in a pool (original order) from a specific source.
    /// </summary>
    Task<List<MediaItem>> GetAllPoolPostsAsync(string source, int poolId, CancellationToken cancellationToken = default);

    // Cache maintenance for e621 (no-op for others)
    void ClearE621SearchCache();
    void ClearE621TagSuggestCache();
    void ClearE621PoolPostsCache();
    void ClearE621FullPoolCache();
    void ClearE621PostDetailsCache();
    void ClearE621PoolDetailsCache();
}

/// <summary>
/// Download service interface
/// </summary>
public interface IDownloadService
{
    /// <summary>
    /// Queue a media item for download
    /// </summary>
    Task<string> QueueDownloadAsync(MediaItem mediaItem, string destinationPath);

    /// <summary>
    /// Queue multiple media items for download
    /// </summary>
    Task<List<string>> QueueMultipleDownloadsAsync(List<MediaItem> mediaItems, string destinationPath);

    /// <summary>
    /// Queue a grouped aggregate download (e.g., a pool) and its children.
    /// Returns the aggregate job ID.
    /// </summary>
    Task<string> QueueAggregateDownloadsAsync(string groupType, List<MediaItem> mediaItems, string destinationPath, string? groupLabel = null);

    /// <summary>
    /// Get all download jobs
    /// </summary>
    Task<List<DownloadJob>> GetDownloadJobsAsync();

    /// <summary>
    /// Get a specific download job by ID
    /// </summary>
    Task<DownloadJob?> GetDownloadJobAsync(string jobId);

    /// <summary>
    /// Pause a download job
    /// </summary>
    Task<bool> PauseDownloadAsync(string jobId);

    /// <summary>
    /// Resume a download job
    /// </summary>
    Task<bool> ResumeDownloadAsync(string jobId);

    /// <summary>
    /// Cancel a download job
    /// </summary>
    Task<bool> CancelDownloadAsync(string jobId);

    /// <summary>
    /// Retry a failed download job
    /// </summary>
    Task<bool> RetryDownloadAsync(string jobId);

    /// <summary>
    /// Event fired when download progress updates
    /// </summary>
    event EventHandler<DownloadJob>? DownloadProgressUpdated;

    /// <summary>
    /// Event fired when download status changes
    /// </summary>
    event EventHandler<DownloadJob>? DownloadStatusChanged;
}

/// <summary>
/// Settings service interface
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Get a setting value
    /// </summary>
    T? GetSetting<T>(string key, T? defaultValue = default);

    /// <summary>
    /// Set a setting value
    /// </summary>
    Task SetSettingAsync<T>(string key, T value);

    /// <summary>
    /// Get all settings
    /// </summary>
    Dictionary<string, object> GetAllSettings();

    /// <summary>
    /// Save settings to storage
    /// </summary>
    Task SaveAsync();

    /// <summary>
    /// Load settings from storage
    /// </summary>
    Task LoadAsync();

    /// <summary>
    /// Event fired when settings change
    /// </summary>
    event EventHandler<string>? SettingChanged;
}
