using System.Text.Json.Serialization;

namespace Furchive.Core.Models;

/// <summary>
/// Represents a unified media item across all supported platforms
/// </summary>
public class MediaItem
{
    public string Id { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty; // e621, furaffinity, inkbunny, weasyl
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string PreviewUrl { get; set; } = string.Empty;
    public string FullImageUrl { get; set; } = string.Empty;
    // When downloaded, absolute local filesystem path to the media (preferred for viewing/animation)
    public string? LocalFilePath { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, List<string>> TagCategories { get; set; } = new();
    public ContentRating Rating { get; set; } = ContentRating.Safe;
    public DateTime CreatedAt { get; set; }
    public int Score { get; set; }
    public int FavoriteCount { get; set; }
    public string FileExtension { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    // Timestamp (UTC) when this record was last fetched from the remote API (for background refresh logic)
    public DateTime? LastFetchedAt { get; set; }
}

/// <summary>
/// Content rating enumeration
/// </summary>
public enum ContentRating
{
    Safe,
    Questionable,
    Explicit
}

/// <summary>
/// Search parameters for querying platforms
/// </summary>
public class SearchParameters
{
    public List<string> IncludeTags { get; set; } = new();
    public List<string> ExcludeTags { get; set; } = new();
    public List<string> Sources { get; set; } = new(); // e621, furaffinity, inkbunny, weasyl
    public List<ContentRating> Ratings { get; set; } = new();
    public string? Artist { get; set; }
    public SortOrder Sort { get; set; } = SortOrder.Newest;
    public int Page { get; set; } = 1;
    public int Limit { get; set; } = 50;
}

/// <summary>
/// Sort order options
/// </summary>
public enum SortOrder
{
    Newest,
    Oldest,
    Score,
    Favorites,
    Random
}

/// <summary>
/// Search results container
/// </summary>
public class SearchResult
{
    public List<MediaItem> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int CurrentPage { get; set; }
    public bool HasNextPage { get; set; }
    public Dictionary<string, string> Errors { get; set; } = new(); // Source -> Error message
}

/// <summary>
/// Download job status
/// </summary>
public class DownloadJob : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public MediaItem MediaItem { get; set; } = new();
    // Stable queue order (monotonically increasing). Not persisted yet; reconstructed from load order.
    public long Sequence { get; set; }
    // Aggregate/group download support
    public bool IsAggregate { get; set; } = false; // true for a synthetic job aggregating child jobs
    public string? ParentId { get; set; } // if set, this is a child of an aggregate job
    public List<string> ChildrenIds { get; set; } = new(); // used only on aggregate jobs
    public string? GroupType { get; set; } // e.g., "pool"
    private string _destinationPath = string.Empty;
    public string DestinationPath { get => _destinationPath; set { if (_destinationPath != value) { _destinationPath = value; OnPropertyChanged(nameof(DestinationPath)); } } }
    private DownloadStatus _status = DownloadStatus.Queued;
    public DownloadStatus Status { get => _status; set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); } } }
    private long _bytesDownloaded;
    public long BytesDownloaded { get => _bytesDownloaded; set { if (_bytesDownloaded != value) { _bytesDownloaded = value; OnPropertyChanged(nameof(BytesDownloaded)); OnPropertyChanged(nameof(ProgressPercent)); } } }
    private long _totalBytes;
    public long TotalBytes { get => _totalBytes; set { if (_totalBytes != value) { _totalBytes = value; OnPropertyChanged(nameof(TotalBytes)); OnPropertyChanged(nameof(ProgressPercent)); } } }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set { if (_errorMessage != value) { _errorMessage = value; OnPropertyChanged(nameof(ErrorMessage)); } } }
    public int RetryCount { get; set; }
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Download status enumeration
/// </summary>
public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Paused,
    Cancelled
}

/// <summary>
/// Platform health status
/// </summary>
public class PlatformHealth
{
    public string Source { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public bool IsAuthenticated { get; set; }
    public int RateLimitRemaining { get; set; }
    public DateTime? RateLimitResetAt { get; set; }
    public string? LastError { get; set; }
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Tag suggestion result
/// </summary>
public class TagSuggestion
{
    public string Tag { get; set; } = string.Empty;
    public int PostCount { get; set; }
    public string Category { get; set; } = string.Empty; // artist, character, species, general, etc.
}
