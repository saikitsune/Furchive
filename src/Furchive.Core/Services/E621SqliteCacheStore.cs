using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Furchive.Core.Interfaces;

namespace Furchive.Core.Services;

/// <summary>
/// SQLite implementation for e621 caches. Each cacheName maps to its own sqlite file under %LocalAppData%/Furchive/cache/e621.
/// Table schema: cache(key TEXT PRIMARY KEY, value TEXT NOT NULL, expiresUtc TEXT NOT NULL)
/// </summary>
public sealed class E621SqliteCacheStore : IE621CacheStore
{
    private readonly string _root;
    private readonly ILogger<E621SqliteCacheStore> _logger;

    public E621SqliteCacheStore(ILogger<E621SqliteCacheStore> logger)
    {
        _logger = logger;
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache", "e621");
        Directory.CreateDirectory(_root);
    }

    private string GetDbPath(string cacheName)
    {
        var safe = string.Join("_", cacheName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return Path.Combine(_root, safe + ".sqlite");
    }

    private SqliteConnection Create(string cacheName) => new($"Data Source={GetDbPath(cacheName)};Cache=Shared");

    private async Task EnsureSchemaAsync(string cacheName)
    {
        await using var conn = Create(cacheName);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS cache (key TEXT PRIMARY KEY, value TEXT NOT NULL, expiresUtc TEXT NOT NULL);";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> GetAsync<T>(string cacheName, string key)
    {
        try
        {
            await EnsureSchemaAsync(cacheName);
            await using var conn = Create(cacheName);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value, expiresUtc FROM cache WHERE key=@k";
            var p = cmd.CreateParameter(); p.ParameterName = "@k"; p.Value = key; cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return default;
            var json = r.GetString(0);
            var expiresStr = r.GetString(1);
            if (!DateTime.TryParse(expiresStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
            {
                // Expiration malformed -> treat as miss
                return default;
            }
            if (DateTime.UtcNow >= expires) return default;
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return default;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache get failed for {CacheName} key {Key}", cacheName, key);
            return default;
        }
    }

    public async Task SetAsync<T>(string cacheName, string key, T value, DateTime expiresUtc)
    {
        try
        {
            await EnsureSchemaAsync(cacheName);
            await using var conn = Create(cacheName);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cache(key,value,expiresUtc) VALUES(@k,@v,@e) ON CONFLICT(key) DO UPDATE SET value=excluded.value, expiresUtc=excluded.expiresUtc";
            var p1 = cmd.CreateParameter(); p1.ParameterName = "@k"; p1.Value = key; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "@v"; p2.Value = JsonSerializer.Serialize(value); cmd.Parameters.Add(p2);
            var p3 = cmd.CreateParameter(); p3.ParameterName = "@e"; p3.Value = expiresUtc.ToString("O"); cmd.Parameters.Add(p3);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache set failed for {CacheName} key {Key}", cacheName, key);
        }
    }

    public async Task ClearAsync(string cacheName)
    {
        try
        {
            var path = GetDbPath(cacheName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache clear failed for {CacheName}", cacheName);
        }
        await Task.CompletedTask;
    }
}
