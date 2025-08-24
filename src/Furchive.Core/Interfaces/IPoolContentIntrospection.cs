using System.Threading;
using System.Threading.Tasks;
using Furchive.Core.Models;

namespace Furchive.Core.Interfaces;

/// <summary>
/// Optional platform capability: ability to introspect pool content counts and media presence without full downloads.
/// Implemented by platforms that can cheaply determine visible post counts and whether any posts contain renderable media.
/// </summary>
public interface IPoolContentIntrospection
{
    /// <summary>
    /// Returns total number of visible (non-deleted) posts in the pool, or null if unknown/error.
    /// </summary>
    Task<int?> GetPoolVisiblePostCountAsync(int poolId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if at least one sampled post in the pool has a usable media URL, false if none, null if unknown/error.
    /// </summary>
    Task<bool?> PoolHasRenderableContentAsync(int poolId, int sample = 10, CancellationToken ct = default);
}

/// <summary>
/// Service that decides which pools should be pruned (removed) based on platform introspection.
/// </summary>
public interface IPoolPruningService
{
    /// <summary>
    /// Returns list of pool ids that should be removed (zero visible posts or no renderable media).
    /// </summary>
    Task<List<int>> DeterminePoolsToPruneAsync(IPlatformApi platform, IEnumerable<PoolInfo> pools, CancellationToken ct = default);
}
