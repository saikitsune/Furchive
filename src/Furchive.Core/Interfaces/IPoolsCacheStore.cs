using Furchive.Core.Models;

namespace Furchive.Core.Interfaces;

public interface IPoolsCacheStore
{
    Task InitializeAsync(CancellationToken ct = default);

    // Pools
    Task<List<PoolInfo>> GetAllPoolsAsync(CancellationToken ct = default);
    Task UpsertPoolsAsync(IEnumerable<PoolInfo> pools, CancellationToken ct = default);
    Task<DateTime?> GetPoolsSavedAtAsync(CancellationToken ct = default);

    // Posts per pool
    Task<List<MediaItem>> GetPoolPostsAsync(int poolId, CancellationToken ct = default);
    Task UpsertPoolPostsAsync(int poolId, IEnumerable<MediaItem> posts, CancellationToken ct = default);

    // App state (moved from settings.json)
    Task SaveLastSessionAsync(string json, CancellationToken ct = default);
    Task<string?> LoadLastSessionAsync(CancellationToken ct = default);
    Task ClearLastSessionAsync(CancellationToken ct = default);
    Task<List<PoolInfo>> GetPinnedPoolsAsync(CancellationToken ct = default);
    Task SavePinnedPoolsAsync(IEnumerable<PoolInfo> pools, CancellationToken ct = default);
}
