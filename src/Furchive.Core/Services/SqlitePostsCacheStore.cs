using Microsoft.Data.Sqlite;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;

namespace Furchive.Core.Services;

/// <summary>
/// Separate SQLite store for per-post metadata (e621 only currently).
/// NOTE: This store NEVER caches search queries or tag combinations; it only stores
/// normalized post records. All searches always go to the live API, and results are
/// then upserted here so subsequent pool loads / detail views can hydrate quickly
/// while still receiving fresh updates each search.
/// </summary>
public sealed class SqlitePostsCacheStore : IPostsCacheStore
{
    private readonly string _dbPath;

    public SqlitePostsCacheStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache", "e621");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "posts_cache.sqlite");
    }

    private SqliteConnection Create() => new($"Data Source={_dbPath};Cache=Shared");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS posts (
  id TEXT PRIMARY KEY,
  source TEXT NOT NULL,
  title TEXT,
  description TEXT,
  artist TEXT,
  preview_url TEXT,
  full_url TEXT,
  source_url TEXT,
  tags TEXT,
    tag_categories TEXT, -- JSON serialized dictionary<string,List<string>> for full category mapping
  rating INTEGER,
  created_at TEXT,
  score INTEGER,
  favorite_count INTEGER,
  file_ext TEXT,
  file_size INTEGER,
  width INTEGER,
  height INTEGER,
  pool_id INTEGER -- nullable; helps retrieving by pool
);
CREATE INDEX IF NOT EXISTS idx_posts_pool ON posts(pool_id);
-- Migration: add tag_categories column if missing (older schemas)
PRAGMA table_info(posts);

";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertPostsAsync(IEnumerable<MediaItem> posts, int? poolId = null, CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
    cmd.CommandText = @"INSERT INTO posts(id,source,title,description,artist,preview_url,full_url,source_url,tags,tag_categories,rating,created_at,score,favorite_count,file_ext,file_size,width,height,pool_id)
VALUES(@id,@source,@title,@desc,@artist,@purl,@furl,@surl,@tags,@tagcats,@rating,@created,@score,@fav,@ext,@fsize,@w,@h,@pool)
ON CONFLICT(id) DO UPDATE SET
 source=excluded.source,
 title=excluded.title,
 description=excluded.description,
 artist=excluded.artist,
 preview_url=excluded.preview_url,
 full_url=excluded.full_url,
 source_url=excluded.source_url,
 tags=excluded.tags,
 tag_categories=excluded.tag_categories,
 rating=excluded.rating,
 created_at=excluded.created_at,
 score=excluded.score,
 favorite_count=excluded.favorite_count,
 file_ext=excluded.file_ext,
 file_size=excluded.file_size,
 width=excluded.width,
 height=excluded.height,
 pool_id=COALESCE(excluded.pool_id, posts.pool_id);";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pSource = cmd.CreateParameter(); pSource.ParameterName = "@source"; cmd.Parameters.Add(pSource);
        var pTitle = cmd.CreateParameter(); pTitle.ParameterName = "@title"; cmd.Parameters.Add(pTitle);
        var pDesc = cmd.CreateParameter(); pDesc.ParameterName = "@desc"; cmd.Parameters.Add(pDesc);
        var pArtist = cmd.CreateParameter(); pArtist.ParameterName = "@artist"; cmd.Parameters.Add(pArtist);
        var pPurl = cmd.CreateParameter(); pPurl.ParameterName = "@purl"; cmd.Parameters.Add(pPurl);
        var pFurl = cmd.CreateParameter(); pFurl.ParameterName = "@furl"; cmd.Parameters.Add(pFurl);
        var pSurl = cmd.CreateParameter(); pSurl.ParameterName = "@surl"; cmd.Parameters.Add(pSurl);
        var pTags = cmd.CreateParameter(); pTags.ParameterName = "@tags"; cmd.Parameters.Add(pTags);
    var pTagCats = cmd.CreateParameter(); pTagCats.ParameterName = "@tagcats"; cmd.Parameters.Add(pTagCats);
    var pRating = cmd.CreateParameter(); pRating.ParameterName = "@rating"; cmd.Parameters.Add(pRating);
        var pCreated = cmd.CreateParameter(); pCreated.ParameterName = "@created"; cmd.Parameters.Add(pCreated);
        var pScore = cmd.CreateParameter(); pScore.ParameterName = "@score"; cmd.Parameters.Add(pScore);
        var pFav = cmd.CreateParameter(); pFav.ParameterName = "@fav"; cmd.Parameters.Add(pFav);
        var pExt = cmd.CreateParameter(); pExt.ParameterName = "@ext"; cmd.Parameters.Add(pExt);
        var pFsize = cmd.CreateParameter(); pFsize.ParameterName = "@fsize"; cmd.Parameters.Add(pFsize);
        var pW = cmd.CreateParameter(); pW.ParameterName = "@w"; cmd.Parameters.Add(pW);
        var pH = cmd.CreateParameter(); pH.ParameterName = "@h"; cmd.Parameters.Add(pH);
        var pPool = cmd.CreateParameter(); pPool.ParameterName = "@pool"; cmd.Parameters.Add(pPool);

        foreach (var m in posts)
        {
            pId.Value = m.Id;
            pSource.Value = m.Source ?? "e621";
            pTitle.Value = m.Title ?? string.Empty;
            pDesc.Value = m.Description ?? string.Empty;
            pArtist.Value = m.Artist ?? string.Empty;
            pPurl.Value = m.PreviewUrl ?? string.Empty;
            pFurl.Value = m.FullImageUrl ?? string.Empty;
            pSurl.Value = m.SourceUrl ?? string.Empty;
            pTags.Value = string.Join('\u001F', m.Tags ?? new()); // unit separator for join
            pTagCats.Value = (m.TagCategories != null && m.TagCategories.Count > 0) ? System.Text.Json.JsonSerializer.Serialize(m.TagCategories) : string.Empty;
            pRating.Value = (int)m.Rating;
            pCreated.Value = m.CreatedAt == default ? string.Empty : m.CreatedAt.ToString("O");
            pScore.Value = m.Score;
            pFav.Value = m.FavoriteCount;
            pExt.Value = m.FileExtension ?? string.Empty;
            pFsize.Value = m.FileSizeBytes;
            pW.Value = m.Width;
            pH.Value = m.Height;
            pPool.Value = poolId.HasValue ? poolId.Value : (object?)DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<List<MediaItem>> GetPoolPostsAsync(int poolId, CancellationToken ct = default)
    {
        var list = new List<MediaItem>();
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, source, title, description, artist, preview_url, full_url, source_url, tags, tag_categories, rating, created_at, score, favorite_count, file_ext, file_size, width, height FROM posts WHERE pool_id=@pid ORDER BY id";
        var p = cmd.CreateParameter(); p.ParameterName = "@pid"; p.Value = poolId; cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new MediaItem
            {
                Id = r.GetString(0),
                Source = r.GetString(1),
                Title = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                Description = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                Artist = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                PreviewUrl = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                FullImageUrl = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                SourceUrl = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                Tags = r.IsDBNull(8) ? new List<string>() : r.GetString(8).Split('\u001F', StringSplitOptions.RemoveEmptyEntries).ToList(),
                TagCategories = r.IsDBNull(9) ? new Dictionary<string, List<string>>() : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(r.GetString(9)) ?? new(),
                Rating = r.IsDBNull(10) ? ContentRating.Safe : (ContentRating)r.GetInt32(10),
                CreatedAt = r.IsDBNull(11) ? default : DateTime.TryParse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : default,
                Score = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                FavoriteCount = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                FileExtension = r.IsDBNull(14) ? string.Empty : r.GetString(14),
                FileSizeBytes = r.IsDBNull(15) ? 0 : r.GetInt64(15),
                Width = r.IsDBNull(16) ? 0 : r.GetInt32(16),
                Height = r.IsDBNull(17) ? 0 : r.GetInt32(17)
            });
        }
        return list;
    }

    public async Task<MediaItem?> GetPostAsync(string postId, CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, source, title, description, artist, preview_url, full_url, source_url, tags, tag_categories, rating, created_at, score, favorite_count, file_ext, file_size, width, height FROM posts WHERE id=@id";
        var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = postId; cmd.Parameters.Add(p);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new MediaItem
            {
                Id = r.GetString(0),
                Source = r.GetString(1),
                Title = r.IsDBNull(2) ? string.Empty : r.GetString(2),
                Description = r.IsDBNull(3) ? string.Empty : r.GetString(3),
                Artist = r.IsDBNull(4) ? string.Empty : r.GetString(4),
                PreviewUrl = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                FullImageUrl = r.IsDBNull(6) ? string.Empty : r.GetString(6),
                SourceUrl = r.IsDBNull(7) ? string.Empty : r.GetString(7),
                Tags = r.IsDBNull(8) ? new List<string>() : r.GetString(8).Split('\u001F', StringSplitOptions.RemoveEmptyEntries).ToList(),
                TagCategories = r.IsDBNull(9) ? new Dictionary<string, List<string>>() : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(r.GetString(9)) ?? new(),
                Rating = r.IsDBNull(10) ? ContentRating.Safe : (ContentRating)r.GetInt32(10),
                CreatedAt = r.IsDBNull(11) ? default : DateTime.TryParse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : default,
                Score = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                FavoriteCount = r.IsDBNull(13) ? 0 : r.GetInt32(13),
                FileExtension = r.IsDBNull(14) ? string.Empty : r.GetString(14),
                FileSizeBytes = r.IsDBNull(15) ? 0 : r.GetInt64(15),
                Width = r.IsDBNull(16) ? 0 : r.GetInt32(16),
                Height = r.IsDBNull(17) ? 0 : r.GetInt32(17)
            };
        }
        return null;
    }
}
