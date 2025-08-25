using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.IO.Compression;
using Microsoft.VisualBasic.FileIO; // For robust CSV parsing (handles quoted newlines)

namespace Furchive.Core.Services;

public sealed class SqlitePoolsCacheStore : IPoolsCacheStore
{
    private readonly string _dbPath;
    private readonly string _lastSessionDbPath;
    private readonly string _pinnedPoolsDbPath;
    private readonly ILogger<SqlitePoolsCacheStore> _logger;

    public SqlitePoolsCacheStore(ILogger<SqlitePoolsCacheStore> logger)
    {
    // Pools list cache now lives under cache/e621 subfolder (posts moved to separate posts_cache.sqlite)
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache", "e621");
        Directory.CreateDirectory(root);
    _dbPath = Path.Combine(root, "pools_list_cache.sqlite");
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
    created_at TEXT NULL,
    updated_at TEXT NULL,
    creator_id INTEGER NULL,
    description TEXT NULL,
    is_active INTEGER NOT NULL DEFAULT 0,
    category TEXT NULL,
    post_ids TEXT NULL,
    post_count INTEGER NOT NULL DEFAULT 0
);
"; // posts now tracked elsewhere; schema extended to align with export
        await cmd.ExecuteNonQueryAsync(ct);

        // Lightweight migration: ensure new columns exist if DB predates extension
        try
        {
            var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(pools)";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var r = await pragma.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct)) existing.Add(r.GetString(1));
            }
            string[] needed = {"created_at","updated_at","creator_id","description","is_active","category","post_ids","post_count"};
            foreach (var col in needed)
            {
                if (!existing.Contains(col))
                {
                    try
                    {
                        string def = col switch {
                            "is_active" => " INTEGER NOT NULL DEFAULT 0",
                            "post_count" => " INTEGER NOT NULL DEFAULT 0",
                            "creator_id" => " INTEGER NULL",
                            _ => " TEXT NULL"
                        };
                        var alter = conn.CreateCommand(); alter.CommandText = $"ALTER TABLE pools ADD COLUMN {col}{def}"; await alter.ExecuteNonQueryAsync(ct);
                    }
                    catch (Exception aex) { _logger.LogDebug(aex, "Add column {Col} failed", col); }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pools schema migration check failed (non-fatal)");
        }

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

    public async Task<List<PoolInfo>> GetAllPoolsDetailedAsync(CancellationToken ct = default)
    {
        var list = new List<PoolInfo>();
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id,name,post_count,created_at,updated_at,creator_id,description,is_active,category,post_ids FROM pools ORDER BY name COLLATE NOCASE";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PoolInfo {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                PostCount = r.GetInt32(2),
                CreatedAt = r.IsDBNull(3) ? null : r.GetString(3),
                UpdatedAt = r.IsDBNull(4) ? null : r.GetString(4),
                CreatorId = r.IsDBNull(5) ? null : r.GetInt32(5),
                Description = r.IsDBNull(6) ? null : r.GetString(6),
                IsActive = !r.IsDBNull(7) && r.GetInt32(7) != 0,
                Category = r.IsDBNull(8) ? null : r.GetString(8),
                PostIdsRaw = r.IsDBNull(9) ? null : r.GetString(9)
            });
        }
        return list;
    }

    public async Task<PoolInfo?> GetPoolByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id,name,post_count,created_at,updated_at,creator_id,description,is_active,category,post_ids FROM pools WHERE id=@id LIMIT 1";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new PoolInfo {
                Id = r.GetInt32(0),
                Name = r.GetString(1),
                PostCount = r.GetInt32(2),
                CreatedAt = r.IsDBNull(3) ? null : r.GetString(3),
                UpdatedAt = r.IsDBNull(4) ? null : r.GetString(4),
                CreatorId = r.IsDBNull(5) ? null : r.GetInt32(5),
                Description = r.IsDBNull(6) ? null : r.GetString(6),
                IsActive = !r.IsDBNull(7) && r.GetInt32(7) != 0,
                Category = r.IsDBNull(8) ? null : r.GetString(8),
                PostIdsRaw = r.IsDBNull(9) ? null : r.GetString(9)
            };
        }
        return null;
    }

    /// <summary>
    /// Downloads the latest pools CSV export from e621 (https://e621.net/db_export) when the local
    /// pools_list_cache.sqlite file does not exist, then populates the pools table with its contents.
    /// Returns (success, exportDateUtc) where exportDateUtc is the date parsed from the export filename.
    /// Only minimal columns (id, name, post_count, is_deleted) are used; deleted or zero-count pools are skipped.
    /// </summary>
    public async Task<(bool success, DateTime? exportDateUtc)> ImportPoolsFromLatestExportAsync(bool force = false, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Starting pools export bootstrap (debug={DebugEnabled})", _logger.IsEnabled(LogLevel.Debug));
            bool hasExistingWithData = false;
            if (File.Exists(_dbPath))
            {
                try
                {
                    await using var checkConn = Create();
                    await checkConn.OpenAsync(ct);
                    var checkCmd = checkConn.CreateCommand();
                    checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='pools'";
                    var hasTable = await checkCmd.ExecuteScalarAsync(ct) != null;
                    if (hasTable)
                    {
                        var cntCmd = checkConn.CreateCommand();
                        cntCmd.CommandText = "SELECT COUNT(1) FROM pools";
                        var cnt = Convert.ToInt32(await cntCmd.ExecuteScalarAsync(ct));
                        hasExistingWithData = cnt > 0;
                        if (hasExistingWithData && !force)
                        {
                            _logger.LogDebug("Skipping pools export import: DB already populated ({Count} rows)", cnt);
                            return (false, null);
                        }
                        else if (!hasExistingWithData)
                        {
                            _logger.LogInformation("Pools DB exists but is empty; proceeding with export import bootstrap");
                        }
                        else if (hasExistingWithData && force)
                        {
                            _logger.LogInformation("Force flag specified; re-importing pools export over existing {Count} rows", cnt);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pools DB emptiness check failed; continuing with import attempt");
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Furchive/1.0 (pools-export)");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/plain");
            // Try today then walk backwards until a file is found (max 14 days)
            DateTime utcToday = DateTime.UtcNow.Date;
            DateTime? foundDate = null;
            string? gzPath = null;
            string? csvPath = null;
            var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
            Directory.CreateDirectory(tempRoot);
            for (int offset = 0; offset < 14; offset++)
            {
                var date = utcToday.AddDays(-offset);
                var fileName = $"pools-{date:yyyy-MM-dd}.csv.gz";
                var url = $"https://e621.net/db_export/{fileName}";
                try
                {
                    _logger.LogDebug("Attempting pools export date {Date} -> {Url}", date.ToString("yyyy-MM-dd"), url);
                    using var respHead = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct); // GET directly (some hosts may not allow HEAD)
                    if (!respHead.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("No export for {Date} (status {StatusCode})", date.ToString("yyyy-MM-dd"), (int)respHead.StatusCode);
                        continue; // try previous day
                    }
                    // Save content
                    gzPath = Path.Combine(tempRoot, fileName);
                    await using (var fs = File.Create(gzPath)) { await respHead.Content.CopyToAsync(fs, ct); }
                    var gzLen = new FileInfo(gzPath).Length;
                    _logger.LogDebug("Downloaded gzip {File} size={SizeBytes} bytes", fileName, gzLen);
                    if (new FileInfo(gzPath).Length < 1024) // sanity check (tiny file probably error page)
                    {
                        _logger.LogDebug("Discarding gzip for {Date}: too small ({Size} bytes)", date.ToString("yyyy-MM-dd"), gzLen);
                        try { File.Delete(gzPath); } catch { }
                        continue;
                    }
                    csvPath = Path.Combine(tempRoot, Path.GetFileNameWithoutExtension(fileName));
                    // Decompress
                    await using (var gzStream = File.OpenRead(gzPath))
                    await using (var gzip = new GZipStream(gzStream, CompressionMode.Decompress))
                    await using (var outFs = File.Create(csvPath))
                    {
                        await gzip.CopyToAsync(outFs, ct);
                    }
                    var csvLen = new FileInfo(csvPath).Length;
                    _logger.LogDebug("Decompressed CSV {CsvFile} size={SizeBytes} bytes", Path.GetFileName(csvPath), csvLen);
                    if (new FileInfo(csvPath).Length < 1024) // sanity for CSV
                    {
                        _logger.LogDebug("Discarding CSV for {Date}: too small ({Size} bytes)", date.ToString("yyyy-MM-dd"), csvLen);
                        try { File.Delete(csvPath); } catch { }
                        continue;
                    }
                    foundDate = date;
                    _logger.LogInformation("Found pools export for {Date}", foundDate.Value.ToString("yyyy-MM-dd"));
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Pools export attempt failed for {Date}", date.ToString("yyyy-MM-dd"));
                }
            }
            if (foundDate == null || csvPath == null)
            {
                _logger.LogWarning("No pools export found in last 14 days; bootstrap skipped");
                return (false, null);
            }
            // Parse CSV using TextFieldParser to support quoted newlines & embedded commas
            var pools = new List<PoolInfo>(60000);
            int totalRows = 0; int skippedMissingId = 0; int skippedInvalidName = 0; int derivedCounts = 0;
            using (var parser = new TextFieldParser(csvPath))
            {
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;
                parser.TrimWhiteSpace = false; // keep raw spacing
                if (parser.EndOfData)
                {
                    _logger.LogWarning("Pools export CSV empty for {Date}", foundDate.Value.ToString("yyyy-MM-dd"));
                    return (false, null);
                }
                string[]? headers = parser.ReadFields();
                if (headers == null)
                {
                    _logger.LogWarning("Pools export CSV header missing for {Date}", foundDate.Value.ToString("yyyy-MM-dd"));
                    return (false, null);
                }
                var map = headers.Select((h,i)=>new {h=h.Trim().Trim('"'),i}).ToList();
                int idxId = map.FirstOrDefault(x=>x.h.Equals("id",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxName = map.FirstOrDefault(x=>x.h.Equals("name",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxCreatedAt = map.FirstOrDefault(x=>x.h.Equals("created_at",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxUpdatedAt = map.FirstOrDefault(x=>x.h.Equals("updated_at",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxCreatorId = map.FirstOrDefault(x=>x.h.Equals("creator_id",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxDescription = map.FirstOrDefault(x=>x.h.Equals("description",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxIsActive = map.FirstOrDefault(x=>x.h.Equals("is_active",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxCategory = map.FirstOrDefault(x=>x.h.Equals("category",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxPostIds = map.FirstOrDefault(x=>x.h.Equals("post_ids",StringComparison.OrdinalIgnoreCase))?.i ?? -1;
                int idxPostCount = map.FirstOrDefault(x=>x.h.Equals("post_count",StringComparison.OrdinalIgnoreCase))?.i ?? -1; // may be absent
                if (idxId < 0 || idxName < 0 || (idxPostCount < 0 && idxPostIds < 0))
                {
                    _logger.LogWarning("Pools export missing required columns (have: {Columns}) for {Date}", string.Join(',', map.Select(m=>m.h)), foundDate.Value.ToString("yyyy-MM-dd"));
                    return (false, null);
                }
                while (!parser.EndOfData)
                {
                    ct.ThrowIfCancellationRequested();
                    string[]? cols = null;
                    try { cols = parser.ReadFields(); } catch (MalformedLineException mlex) { _logger.LogDebug(mlex, "Malformed CSV line skipped"); continue; }
                    if (cols == null) continue; totalRows++;
                    if (idxId >= cols.Length || !int.TryParse(cols[idxId], out var id)) { skippedMissingId++; continue; }
                    var nameRaw = idxName < cols.Length ? cols[idxName] : null;
                    if (string.IsNullOrWhiteSpace(nameRaw)) { skippedInvalidName++; continue; }
                    var name = nameRaw.Trim('"');
                    int pc = 0;
                    if (idxPostCount >= 0 && idxPostCount < cols.Length)
                    {
                        if (!int.TryParse(cols[idxPostCount], out pc)) pc = 0;
                    }
                    if (pc == 0 && idxPostIds >= 0 && idxPostIds < cols.Length)
                    {
                        pc = CountPostIds(cols[idxPostIds]); derivedCounts++;
                    }
                    string? postIdsRaw = (idxPostIds >= 0 && idxPostIds < cols.Length) ? cols[idxPostIds] : null;
                    if (postIdsRaw != null && postIdsRaw.Equals("NULL", StringComparison.OrdinalIgnoreCase)) postIdsRaw = null;
                    int? creatorId = null;
                    if (idxCreatorId >= 0 && idxCreatorId < cols.Length && int.TryParse(cols[idxCreatorId], out var cid)) creatorId = cid;
                    static string? Normalize(string? s)
                    {
                        if (string.IsNullOrWhiteSpace(s)) return null;
                        var trimmed = s.Trim();
                        return trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
                    }
                    var createdAt = Normalize(idxCreatedAt >= 0 && idxCreatedAt < cols.Length ? cols[idxCreatedAt] : null);
                    var updatedAt = Normalize(idxUpdatedAt >= 0 && idxUpdatedAt < cols.Length ? cols[idxUpdatedAt] : null);
                    var description = Normalize(idxDescription >= 0 && idxDescription < cols.Length ? cols[idxDescription] : null);
                    var category = Normalize(idxCategory >= 0 && idxCategory < cols.Length ? cols[idxCategory] : null);
                    bool isActive = true;
                    if (idxIsActive >= 0 && idxIsActive < cols.Length)
                    {
                        var flag = cols[idxIsActive].Trim();
                        isActive = !(flag.Equals("f", StringComparison.OrdinalIgnoreCase) || flag.Equals("false", StringComparison.OrdinalIgnoreCase) || flag == "0");
                    }
                    pools.Add(new PoolInfo {
                        Id = id,
                        Name = name,
                        PostCount = pc,
                        PostIdsRaw = postIdsRaw,
                        CreatorId = creatorId,
                        CreatedAt = createdAt,
                        UpdatedAt = updatedAt,
                        Description = description,
                        IsActive = isActive,
                        Category = category
                    });
                }
            }
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                int nnCreated = pools.Count(p => !string.IsNullOrEmpty(p.CreatedAt));
                int nnUpdated = pools.Count(p => !string.IsNullOrEmpty(p.UpdatedAt));
                int nnCreator = pools.Count(p => p.CreatorId.HasValue);
                int nnDesc = pools.Count(p => !string.IsNullOrEmpty(p.Description));
                int nnCat = pools.Count(p => !string.IsNullOrEmpty(p.Category));
                int nnPostIds = pools.Count(p => !string.IsNullOrEmpty(p.PostIdsRaw));
                _logger.LogDebug("Field population stats: created_at={Created}/{Total}, updated_at={Updated}/{Total}, creator_id={Creator}/{Total}, description={Desc}/{Total}, category={Cat}/{Total}, post_ids={PostIds}/{Total}", nnCreated, pools.Count, nnUpdated, pools.Count, nnCreator, pools.Count, nnDesc, pools.Count, nnCat, pools.Count, nnPostIds, pools.Count);
            }
            _logger.LogInformation("Parsed {Count} pools (rows={Rows}, derivedCounts={Derived}, skippedMissingId={SkippedId}, skippedInvalidName={SkippedName}) from export {Date}", pools.Count, totalRows, derivedCounts, skippedMissingId, skippedInvalidName, foundDate.Value.ToString("yyyy-MM-dd"));
            // Persist pools list
            await UpsertPoolsAsync(pools, ct);
            _logger.LogInformation("Pools export bootstrap complete: {Count} pools stored", pools.Count);
            // Overwrite meta timestamp to the export date (so incremental API update can fetch newer pools since export)
            try
            {
                await using var conn = Create();
                await conn.OpenAsync(ct);
                var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO meta(key,value) VALUES('pools_saved_at', @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
                var p = cmd.CreateParameter(); p.ParameterName = "@v"; p.Value = foundDate.Value.ToString("O"); cmd.Parameters.Add(p);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch { }
            return (true, foundDate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImportPoolsFromLatestExportAsync failed");
            return (false, null);
        }
    }

    private static int CountPostIds(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return 0;
        // post_ids likely space-separated (or comma). Count tokens without allocating large arrays.
        int count = 0;
        bool inToken = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == ',' || c == ';')
            {
                if (inToken) { count++; inToken = false; }
            }
            else
            {
                inToken = true;
            }
        }
        if (inToken) count++;
        return count;
    }

    // Simple CSV splitter supporting quotes and escaped quotes
    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        if (line == null) return result;
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else { inQuotes = !inQuotes; }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(sb.ToString()); sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result;
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
        cmd.CommandText = @"INSERT OR IGNORE INTO pools
            (id,name,created_at,updated_at,creator_id,description,is_active,category,post_ids,post_count)
            VALUES(@id,@name,@created_at,@updated_at,@creator_id,@description,@is_active,@category,@post_ids,@post_count)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pName = cmd.CreateParameter(); pName.ParameterName = "@name"; cmd.Parameters.Add(pName);
        var pCreated = cmd.CreateParameter(); pCreated.ParameterName = "@created_at"; cmd.Parameters.Add(pCreated);
        var pUpdated = cmd.CreateParameter(); pUpdated.ParameterName = "@updated_at"; cmd.Parameters.Add(pUpdated);
        var pCreator = cmd.CreateParameter(); pCreator.ParameterName = "@creator_id"; cmd.Parameters.Add(pCreator);
        var pDesc = cmd.CreateParameter(); pDesc.ParameterName = "@description"; cmd.Parameters.Add(pDesc);
        var pIsActive = cmd.CreateParameter(); pIsActive.ParameterName = "@is_active"; cmd.Parameters.Add(pIsActive);
        var pCategory = cmd.CreateParameter(); pCategory.ParameterName = "@category"; cmd.Parameters.Add(pCategory);
        var pPostIds = cmd.CreateParameter(); pPostIds.ParameterName = "@post_ids"; cmd.Parameters.Add(pPostIds);
        var pCnt = cmd.CreateParameter(); pCnt.ParameterName = "@post_count"; cmd.Parameters.Add(pCnt);
        int duplicateCount = 0;
        var seen = new HashSet<int>();
        foreach (var p in pools)
        {
            if (!seen.Add(p.Id)) { duplicateCount++; continue; }
            pId.Value = p.Id;
            pName.Value = p.Name ?? string.Empty;
            pCreated.Value = (object?)p.CreatedAt ?? DBNull.Value;
            pUpdated.Value = (object?)p.UpdatedAt ?? DBNull.Value;
            pCreator.Value = p.CreatorId.HasValue ? p.CreatorId.Value : DBNull.Value;
            pDesc.Value = (object?)p.Description ?? DBNull.Value;
            pIsActive.Value = p.IsActive ? 1 : 0;
            pCategory.Value = (object?)p.Category ?? DBNull.Value;
            pPostIds.Value = (object?)p.PostIdsRaw ?? DBNull.Value;
            pCnt.Value = p.PostCount;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (duplicateCount > 0)
        {
            try { _logger.LogDebug("Ignored {DupCount} duplicate pool id rows during upsert", duplicateCount); } catch { }
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

    // Pool post storage removed from this class; use IPostsCacheStore instead

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
