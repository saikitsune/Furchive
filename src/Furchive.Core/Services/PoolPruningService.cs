using System.Collections.Concurrent;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;

namespace Furchive.Core.Services;

public class PoolPruningService : IPoolPruningService
{
    private readonly ILogger<PoolPruningService> _logger;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(30);
    private class Entry { public DateTime At; public bool HasContent; public int Visible; }
    private readonly ConcurrentDictionary<int, Entry> _cache = new();

    public PoolPruningService(ILogger<PoolPruningService> logger) { _logger = logger; }

    public async Task<List<int>> DeterminePoolsToPruneAsync(IPlatformApi platform, IEnumerable<PoolInfo> pools, CancellationToken ct = default)
    {
        var result = new ConcurrentBag<int>();
        if (platform is not IPoolContentIntrospection introspection) return new();
        var semaphore = new SemaphoreSlim(4);
        var tasks = pools.Select(async p => {
            await semaphore.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;
                if (_cache.TryGetValue(p.Id, out var cached) && DateTime.UtcNow - cached.At < _cacheTtl)
                {
                    if (cached.Visible == 0 || !cached.HasContent) result.Add(p.Id);
                    return;
                }
                var visible = await introspection.GetPoolVisiblePostCountAsync(p.Id, ct) ?? -1;
                if (visible == 0)
                {
                    _cache[p.Id] = new Entry { At = DateTime.UtcNow, HasContent = false, Visible = 0 };
                    result.Add(p.Id); return;
                }
                if (visible > 0)
                {
                    var hasContent = await introspection.PoolHasRenderableContentAsync(p.Id, 10, ct) ?? true; // assume true on unknown
                    _cache[p.Id] = new Entry { At = DateTime.UtcNow, HasContent = hasContent, Visible = visible };
                    if (!hasContent) result.Add(p.Id);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Pool prune check failed for {PoolId}", p.Id); }
            finally { semaphore.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); } catch { }
        return result.Distinct().ToList();
    }
}
