using System;
using System.Threading.Tasks;

namespace Furchive.Core.Interfaces;

/// <summary>
/// Generic SQLite-backed cache for e621 data. Values are stored as JSON with per-entry expirations.
/// Implementations should store entries in separate SQLite files per cacheName.
/// </summary>
public interface IE621CacheStore
{
    /// <summary>
    /// Try get a cached value. Returns default(T) if missing or expired.
    /// </summary>
    Task<T?> GetAsync<T>(string cacheName, string key);

    /// <summary>
    /// Upsert a cached value with an absolute expiration time.
    /// </summary>
    Task SetAsync<T>(string cacheName, string key, T value, DateTime expiresUtc);

    /// <summary>
    /// Clear all entries for a given cache.
    /// </summary>
    Task ClearAsync(string cacheName);
}
