using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Linq;
using System.Collections.Concurrent;

namespace Furchive.Core.Services;

/// <summary>
/// Unified API service that aggregates multiple platform APIs
/// </summary>
public class UnifiedApiService : IUnifiedApiService
{
    private readonly Dictionary<string, IPlatformApi> _platforms = new();
    private readonly ILogger<UnifiedApiService> _logger;

    // Static registry to allow cross-instance access (e.g., Settings vs Main)
    private static readonly ConcurrentDictionary<string, IPlatformApi> s_platforms = new(StringComparer.OrdinalIgnoreCase);

    public UnifiedApiService(ILogger<UnifiedApiService> logger)
    {
        _logger = logger;
    }

    public void RegisterPlatform(IPlatformApi platformApi)
    {
        _platforms[platformApi.PlatformName] = platformApi;
    s_platforms[platformApi.PlatformName] = platformApi; // ensure globally discoverable
        _logger.LogInformation("Registered platform: {Platform}", platformApi.PlatformName);
    }

    public async Task<List<PoolInfo>> GetPoolsAsync(string source, CancellationToken cancellationToken = default)
    {
        if (!_platforms.TryGetValue(source, out var api)) return new List<PoolInfo>();
        try { return await api.GetPoolsAsync(cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPools failed for {Source}", source);
            return new List<PoolInfo>();
        }
    }

    public async Task<List<PoolInfo>> GetPoolsAsync(string source, IProgress<(int current, int? total)>? progress, CancellationToken cancellationToken = default)
    {
        if (!_platforms.TryGetValue(source, out var api)) return new List<PoolInfo>();
        try
        {
            // If platform implements the progress overload, use it; otherwise call without progress and report final
            var supports = api.GetType().GetMethod("GetPoolsAsync", new[] { typeof(IProgress<(int current, int? total)>), typeof(CancellationToken) }) != null;
            if (supports)
            {
                var task = (Task<List<PoolInfo>>)api.GetType().GetMethod("GetPoolsAsync", new[] { typeof(IProgress<(int current, int? total)>), typeof(CancellationToken) })!
                    .Invoke(api, new object?[] { progress, cancellationToken })!;
                return await task;
            }
            else
            {
                var list = await api.GetPoolsAsync(cancellationToken);
                progress?.Report((list.Count, list.Count));
                return list;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPools (with progress) failed for {Source}", source);
            return new List<PoolInfo>();
        }
    }

    public async Task<SearchResult> GetPoolPostsAsync(string source, int poolId, int page = 1, int limit = 50, CancellationToken cancellationToken = default)
    {
        if (!_platforms.TryGetValue(source, out var api)) return new SearchResult { Items = new(), CurrentPage = page };
        try
        {
            return await api.GetPoolPostsAsync(poolId, page, limit, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPoolPosts failed for {Source} pool {PoolId}", source, poolId);
            return new SearchResult { Items = new(), CurrentPage = page, Errors = { [source] = ex.Message } };
        }
    }

    public async Task<List<PoolInfo>> GetPoolsUpdatedSinceAsync(string source, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        if (!_platforms.TryGetValue(source, out var api)) return new List<PoolInfo>();
        try { return await api.GetPoolsUpdatedSinceAsync(sinceUtc, cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPoolsUpdatedSince failed for {Source}", source);
            return new List<PoolInfo>();
        }
    }

    public async Task<List<MediaItem>> GetAllPoolPostsAsync(string source, int poolId, CancellationToken cancellationToken = default)
    {
        if (!_platforms.TryGetValue(source, out var api)) return new List<MediaItem>();
        try { return await api.GetAllPoolPostsAsync(poolId, cancellationToken); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllPoolPosts failed for {Source} pool {PoolId}", source, poolId);
            return new List<MediaItem>();
        }
    }

    public async Task<Dictionary<string, PlatformHealth>> GetAllPlatformHealthAsync()
    {
        var healthTasks = _platforms.Values.Select(async platform =>
        {
            try
            {
                var health = await platform.GetHealthAsync();
                return (platform.PlatformName, health);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking health for platform {Platform}", platform.PlatformName);
                return (platform.PlatformName, new PlatformHealth
                {
                    Source = platform.PlatformName,
                    IsAvailable = false,
                    LastError = ex.Message
                });
            }
        });

        var results = await Task.WhenAll(healthTasks);
        return results.ToDictionary(r => r.PlatformName, r => r.Item2);
    }

    public async Task<SearchResult> SearchAsync(SearchParameters parameters)
    {
        var enabledPlatforms = parameters.Sources.Any() 
            ? _platforms.Where(p => parameters.Sources.Contains(p.Key)) 
            : _platforms;

        var searchTasks = enabledPlatforms.Select(async platform =>
        {
            try
            {
                var platformParams = new SearchParameters
                {
                    IncludeTags = parameters.IncludeTags,
                    ExcludeTags = parameters.ExcludeTags,
                    Ratings = parameters.Ratings,
                    Artist = parameters.Artist,
                    Sort = parameters.Sort,
                    Page = parameters.Page,
                    Limit = Math.Min(parameters.Limit, 100) // Cap per platform
                };

                return await platform.Value.SearchAsync(platformParams);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching platform {Platform}", platform.Key);
                return new SearchResult
                {
                    Errors = { [platform.Key] = ex.Message }
                };
            }
        });

        var results = await Task.WhenAll(searchTasks);

        // Aggregate results
        var aggregatedResult = new SearchResult
        {
            CurrentPage = parameters.Page,
            HasNextPage = results.Any(r => r.HasNextPage)
        };

        foreach (var result in results)
        {
            aggregatedResult.Items.AddRange(result.Items);
            aggregatedResult.TotalCount += result.TotalCount;
            
            foreach (var error in result.Errors)
            {
                aggregatedResult.Errors[error.Key] = error.Value;
            }
        }

        // Sort combined results
        aggregatedResult.Items = parameters.Sort switch
        {
            SortOrder.Newest => aggregatedResult.Items.OrderByDescending(i => i.CreatedAt).ToList(),
            SortOrder.Oldest => aggregatedResult.Items.OrderBy(i => i.CreatedAt).ToList(),
            SortOrder.Score => aggregatedResult.Items.OrderByDescending(i => i.Score).ToList(),
            SortOrder.Favorites => aggregatedResult.Items.OrderByDescending(i => i.FavoriteCount).ToList(),
            SortOrder.Random => aggregatedResult.Items.OrderBy(i => Guid.NewGuid()).ToList(),
            _ => aggregatedResult.Items
        };

        // Apply limit to final results
        if (aggregatedResult.Items.Count > parameters.Limit)
        {
            aggregatedResult.Items = aggregatedResult.Items.Take(parameters.Limit).ToList();
        }

        return aggregatedResult;
    }

    public async Task<MediaItem?> GetMediaDetailsAsync(string source, string id)
    {
        if (!_platforms.TryGetValue(source, out var platform))
        {
            _logger.LogWarning("Platform {Source} not found", source);
            return null;
        }

        try
        {
            return await platform.GetMediaDetailsAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting media details from {Source} for ID {Id}", source, id);
            return null;
        }
    }

    public async Task<List<TagSuggestion>> GetTagSuggestionsAsync(string query, List<string> sources, int limit = 10)
    {
        var enabledPlatforms = sources.Any()
            ? _platforms.Where(p => sources.Contains(p.Key))
            : _platforms;

        var suggestionTasks = enabledPlatforms.Select(async platform =>
        {
            try
            {
                return await platform.Value.GetTagSuggestionsAsync(query, limit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tag suggestions from {Platform}", platform.Key);
                return new List<TagSuggestion>();
            }
        });

        var results = await Task.WhenAll(suggestionTasks);
        
        // Combine and deduplicate suggestions
        var allSuggestions = results.SelectMany(r => r)
            .GroupBy(s => s.Tag.ToLowerInvariant())
            .Select(g => new TagSuggestion
            {
                Tag = g.First().Tag,
                PostCount = g.Sum(s => s.PostCount),
                Category = g.First().Category
            })
            .OrderByDescending(s => s.PostCount)
            .Take(limit)
            .ToList();

        return allSuggestions;
    }

    /// <summary>
    /// Resolve category for a single tag using the first platform that returns a value.
    /// Currently primarily e621. Platforms that don't implement the call return null.
    /// </summary>
    public async Task<string?> GetTagCategoryAsync(string tag, List<string> sources)
    {
        try
        {
            var enabledPlatforms = sources.Any()
                ? _platforms.Where(p => sources.Contains(p.Key))
                : _platforms;
            foreach (var p in enabledPlatforms)
            {
                try
                {
                    var cat = await p.Value.GetTagCategoryAsync(tag);
                    if (!string.IsNullOrWhiteSpace(cat)) return cat;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tag category lookup failed on {Platform} for {Tag}", p.Key, tag);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unified tag category lookup failed for {Tag}", tag);
        }
        return null;
    }

    // Cache maintenance passthroughs for E621 (no-op for others)
    public void ClearE621SearchCache()
    {
        if (_platforms.TryGetValue("e621", out var api))
        {
            var m = api.GetType().GetMethod("ClearSearchCache");
            m?.Invoke(api, null);
        }
    }
    public void ClearE621TagSuggestCache()
    {
        if (_platforms.TryGetValue("e621", out var api))
        {
            var m = api.GetType().GetMethod("ClearTagSuggestCache");
            m?.Invoke(api, null);
        }
    }
    public void ClearE621PoolPostsCache()
    {
        if (_platforms.TryGetValue("e621", out var api))
        {
            var m = api.GetType().GetMethod("ClearPoolPostsCache");
            m?.Invoke(api, null);
        }
    }
    public void ClearE621FullPoolCache()
    {
        if (_platforms.TryGetValue("e621", out var api))
        {
            var m = api.GetType().GetMethod("ClearFullPoolCache");
            m?.Invoke(api, null);
        }
    }
    public void ClearE621PostDetailsCache()
    {
        if (_platforms.TryGetValue("e621", out var api))
        {
            var m = api.GetType().GetMethod("ClearPostDetailsCache");
            m?.Invoke(api, null);
        }
    }
    public void ClearE621PoolDetailsCache()
    {
    if (_platforms.TryGetValue("e621", out var api) || s_platforms.TryGetValue("e621", out api))
        {
            var m = api.GetType().GetMethod("ClearPoolDetailsCache");
            m?.Invoke(api, null);
        }
    }

    // Lightweight metrics passthrough (used by Settings Admin view)
    public object? GetE621CacheMetrics()
    {
    if (_platforms.TryGetValue("e621", out var api) || s_platforms.TryGetValue("e621", out api))
        {
            var m = api.GetType().GetMethod("GetCacheMetrics");
            return m?.Invoke(api, null);
        }
        return null;
    }

    // Persistent cache passthroughs
    public void LoadE621PersistentCacheIfEnabled()
    {
        if (_platforms.TryGetValue("e621", out var api) || s_platforms.TryGetValue("e621", out api))
        {
            var m = api.GetType().GetMethod("LoadPersistentCacheIfEnabled");
            m?.Invoke(api, null);
        }
    }
    public void SaveE621PersistentCacheIfEnabled()
    {
        if (_platforms.TryGetValue("e621", out var api) || s_platforms.TryGetValue("e621", out api))
        {
            var m = api.GetType().GetMethod("SavePersistentCacheIfEnabled");
            m?.Invoke(api, null);
        }
    }
}
