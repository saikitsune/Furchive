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
