namespace Furchive.Core.Interfaces;

/// <summary>
/// Thumbnail cache service to store and retrieve preview images on disk.
/// </summary>
public interface IThumbnailCacheService
{
    /// <summary>
    /// Get a cached thumbnail path or download and cache it.
    /// </summary>
    /// <param name="thumbnailUrl">URL of the thumbnail image</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Local file path to the cached thumbnail</returns>
    Task<string> GetOrAddAsync(string thumbnailUrl, CancellationToken cancellationToken = default);
    /// <summary>
    /// If the thumbnail is already cached, return its local path; otherwise null.
    /// </summary>
    string? TryGetCachedPath(string thumbnailUrl);

    /// <summary>
    /// Get cache usage in bytes.
    /// </summary>
    /// <returns>Total bytes used by the cache</returns>
    long GetUsedBytes();

    /// <summary>
    /// Get the cache root path.
    /// </summary>
    string GetCachePath();

    /// <summary>
    /// Clear cache contents.
    /// </summary>
    Task ClearAsync();
}
