using Furchive.Core.Models;

namespace Furchive.Core.Interfaces;

/// <summary>
/// Stores individual post metadata (one row per post) sourced from platform post JSON.
/// Replaces prior pool_posts table. Supports optional association to a pool via pool_id.
/// </summary>
public interface IPostsCacheStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertPostsAsync(IEnumerable<MediaItem> posts, int? poolId = null, CancellationToken ct = default);
    Task<List<MediaItem>> GetPoolPostsAsync(int poolId, CancellationToken ct = default);
    Task<MediaItem?> GetPostAsync(string postId, CancellationToken ct = default);
}
