using Furchive.Core.Models;

namespace Furchive.Core.Interfaces;

public interface IPoolsCacheStore
{
    Task InitializeAsync(CancellationToken ct = default);

    // Pools
    Task<List<PoolInfo>> GetAllPoolsAsync(CancellationToken ct = default);
    // Detailed version returns all stored columns (id, name, post_count, created_at, updated_at, creator_id, description, is_active, category, post_ids)
    Task<List<PoolInfo>> GetAllPoolsDetailedAsync(CancellationToken ct = default);
    Task<PoolInfo?> GetPoolByIdAsync(int id, CancellationToken ct = default);
    Task UpsertPoolsAsync(IEnumerable<PoolInfo> pools, CancellationToken ct = default);
    Task<DateTime?> GetPoolsSavedAtAsync(CancellationToken ct = default);

    // Posts per pool removed; handled by IPostsCacheStore now

    // App state (moved from settings.json)
    Task SaveLastSessionAsync(string json, CancellationToken ct = default);
    Task<string?> LoadLastSessionAsync(CancellationToken ct = default);
    Task ClearLastSessionAsync(CancellationToken ct = default);
    Task<List<PoolInfo>> GetPinnedPoolsAsync(CancellationToken ct = default);
    Task SavePinnedPoolsAsync(IEnumerable<PoolInfo> pools, CancellationToken ct = default);
}
