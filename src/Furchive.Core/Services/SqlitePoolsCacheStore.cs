using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;

namespace Furchive.Core.Services;

public sealed class SqlitePoolsCacheStore : IPoolsCacheStore
{
    private readonly string _dbPath;
    private readonly string _lastSessionDbPath;
    private readonly string _pinnedPoolsDbPath;
    private readonly ILogger<SqlitePoolsCacheStore> _logger;

    public SqlitePoolsCacheStore(ILogger<SqlitePoolsCacheStore> logger)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "pools_cache.sqlite");
        _lastSessionDbPath = Path.Combine(root, "last_session.sqlite");
        _pinnedPoolsDbPath = Path.Combine(root, "pinned_pools.sqlite");
        _logger = logger;
    }

    private SqliteConnection Create() => new($"Data Source={_dbPath};Cache=Shared");
    private SqliteConnection CreateLastSession() => new($"Data Source={_lastSessionDbPath};Cache=Shared");
    private SqliteConnection CreatePinnedPools() => new($"Data Source={_pinnedPoolsDbPath};Cache=Shared");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Main pools DB
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
CREATE TABLE IF NOT EXISTS pools (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  post_count INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS pool_posts (
  pool_id INTEGER NOT NULL,
  post_id TEXT NOT NULL,
  source TEXT NOT NULL,
  title TEXT,
  artist TEXT,
  preview_url TEXT,
  full_url TEXT,
  file_ext TEXT,
  PRIMARY KEY (pool_id, post_id)
);
";
        await cmd.ExecuteNonQueryAsync(ct);

        // Last session DB
        await using (var conn2 = CreateLastSession())
        {
            await conn2.OpenAsync(ct);
            var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = @"CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);";
            await cmd2.ExecuteNonQueryAsync(ct);
        }

        // Pinned pools DB
        await using (var conn3 = CreatePinnedPools())
        {
            await conn3.OpenAsync(ct);
            var cmd3 = conn3.CreateCommand();
            cmd3.CommandText = @"
CREATE TABLE IF NOT EXISTS pinned_pools (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    post_count INTEGER NOT NULL
);";
            await cmd3.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<List<PoolInfo>> GetAllPoolsAsync(CancellationToken ct = default)
    {
        var list = new List<PoolInfo>();
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, post_count FROM pools ORDER BY name COLLATE NOCASE";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new PoolInfo { Id = reader.GetInt32(0), Name = reader.GetString(1), PostCount = reader.GetInt32(2) });
        }
        return list;
    }

    public async Task UpsertPoolsAsync(IEnumerable<PoolInfo> pools, CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Replace the entire set to guarantee consistency
        var del = conn.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM pools";
        await del.ExecuteNonQueryAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO pools(id,name,post_count) VALUES(@id,@name,@cnt)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pName = cmd.CreateParameter(); pName.ParameterName = "@name"; cmd.Parameters.Add(pName);
        var pCnt = cmd.CreateParameter(); pCnt.ParameterName = "@cnt"; cmd.Parameters.Add(pCnt);
        foreach (var p in pools)
        {
            pId.Value = p.Id; pName.Value = p.Name ?? string.Empty; pCnt.Value = p.PostCount;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // update meta timestamp
        var meta = conn.CreateCommand();
        meta.Transaction = (SqliteTransaction)tx;
        meta.CommandText = "INSERT INTO meta(key,value) VALUES('pools_saved_at', @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        var v = meta.CreateParameter(); v.ParameterName = "@v"; v.Value = DateTime.UtcNow.ToString("O"); meta.Parameters.Add(v);
        await meta.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task<DateTime?> GetPoolsSavedAtAsync(CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='pools_saved_at'";
        var val = await cmd.ExecuteScalarAsync(ct) as string;
        if (DateTime.TryParse(val, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)) return dt;
        return null;
    }

    public async Task<List<MediaItem>> GetPoolPostsAsync(int poolId, CancellationToken ct = default)
    {
        var list = new List<MediaItem>();
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT post_id, source, title, artist, preview_url, full_url, file_ext FROM pool_posts WHERE pool_id=@pid ORDER BY post_id";
        var p = cmd.CreateParameter(); p.ParameterName = "@pid"; p.Value = poolId; cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new MediaItem
            {
                Id = r.GetString(0),
                Source = r.GetString(1),
                Title = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                Artist = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                PreviewUrl = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                FullImageUrl = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                FileExtension = r.IsDBNull(6) ? string.Empty : r.GetString(6)
            });
        }
        return list;
    }

    public async Task UpsertPoolPostsAsync(int poolId, IEnumerable<MediaItem> posts, CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var del = conn.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM pool_posts WHERE pool_id=@pid";
        var dp = del.CreateParameter(); dp.ParameterName = "@pid"; dp.Value = poolId; del.Parameters.Add(dp);
        await del.ExecuteNonQueryAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = @"INSERT INTO pool_posts(pool_id,post_id,source,title,artist,preview_url,full_url,file_ext) 
VALUES(@pid,@id,@src,@title,@artist,@purl,@furl,@ext)";
        var pPid = cmd.CreateParameter(); pPid.ParameterName = "@pid"; cmd.Parameters.Add(pPid);
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pSrc = cmd.CreateParameter(); pSrc.ParameterName = "@src"; cmd.Parameters.Add(pSrc);
        var pTitle = cmd.CreateParameter(); pTitle.ParameterName = "@title"; cmd.Parameters.Add(pTitle);
        var pArtist = cmd.CreateParameter(); pArtist.ParameterName = "@artist"; cmd.Parameters.Add(pArtist);
        var pPurl = cmd.CreateParameter(); pPurl.ParameterName = "@purl"; cmd.Parameters.Add(pPurl);
        var pFurl = cmd.CreateParameter(); pFurl.ParameterName = "@furl"; cmd.Parameters.Add(pFurl);
        var pExt = cmd.CreateParameter(); pExt.ParameterName = "@ext"; cmd.Parameters.Add(pExt);

        foreach (var m in posts)
        {
            pPid.Value = poolId;
            pId.Value = m.Id;
            pSrc.Value = m.Source ?? "e621";
            pTitle.Value = m.Title ?? string.Empty;
            pArtist.Value = m.Artist ?? string.Empty;
            pPurl.Value = m.PreviewUrl ?? string.Empty;
            pFurl.Value = m.FullImageUrl ?? string.Empty;
            pExt.Value = m.FileExtension ?? string.Empty;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // App state: LastSession JSON and PinnedPools
    public async Task SaveLastSessionAsync(string json, CancellationToken ct = default)
    {
    await using var conn = CreateLastSession();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES('last_session', @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        var p = cmd.CreateParameter(); p.ParameterName = "@v"; p.Value = json ?? string.Empty; cmd.Parameters.Add(p);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> LoadLastSessionAsync(CancellationToken ct = default)
    {
    await using var conn = CreateLastSession();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "SELECT value FROM meta WHERE key='last_session'";
        var val = await cmd.ExecuteScalarAsync(ct) as string;
        return string.IsNullOrWhiteSpace(val) ? null : val;
    }

    public async Task ClearLastSessionAsync(CancellationToken ct = default)
    {
    await using var conn = CreateLastSession();
        await conn.OpenAsync(ct);
    var cmd = conn.CreateCommand();
    cmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);";
    await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "DELETE FROM meta WHERE key='last_session'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<PoolInfo>> GetPinnedPoolsAsync(CancellationToken ct = default)
    {
        var list = new List<PoolInfo>();
    await using var conn = CreatePinnedPools();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, post_count FROM pinned_pools ORDER BY id";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PoolInfo { Id = r.GetInt32(0), Name = r.GetString(1), PostCount = r.GetInt32(2) });
        }
        return list;
    }

    public async Task SavePinnedPoolsAsync(IEnumerable<PoolInfo> pools, CancellationToken ct = default)
    {
    await using var conn = CreatePinnedPools();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var del = conn.CreateCommand();
        del.Transaction = (SqliteTransaction)tx;
        del.CommandText = "DELETE FROM pinned_pools";
        await del.ExecuteNonQueryAsync(ct);

        var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = "INSERT INTO pinned_pools(id,name,post_count) VALUES(@id,@name,@cnt)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pName = cmd.CreateParameter(); pName.ParameterName = "@name"; cmd.Parameters.Add(pName);
        var pCnt = cmd.CreateParameter(); pCnt.ParameterName = "@cnt"; cmd.Parameters.Add(pCnt);
        foreach (var p in pools)
        {
            pId.Value = p.Id; pName.Value = p.Name ?? string.Empty; pCnt.Value = p.PostCount;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
