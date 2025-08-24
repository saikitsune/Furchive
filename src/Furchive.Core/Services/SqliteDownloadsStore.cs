using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Furchive.Core.Services;

public sealed class SqliteDownloadsStore : IDownloadsStore
{
    private readonly string _dbPath;
    private readonly ILogger<SqliteDownloadsStore> _logger;

    public SqliteDownloadsStore(ILogger<SqliteDownloadsStore> logger)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive");
        Directory.CreateDirectory(root);
        _dbPath = Path.Combine(root, "downloads.sqlite");
        _logger = logger;
    }

    private SqliteConnection Create() => new($"Data Source={_dbPath};Cache=Shared");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = Create();
        await conn.OpenAsync(ct);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS downloads (
  id TEXT PRIMARY KEY,
  source TEXT,
  media_id TEXT,
  title TEXT,
  artist TEXT,
  destination_path TEXT,
  status TEXT,
  bytes_downloaded INTEGER,
  total_bytes INTEGER,
  queued_at TEXT,
  started_at TEXT,
  completed_at TEXT,
  error_message TEXT
);";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(DownloadJob job, CancellationToken ct = default)
    {
        try
        {
            await using var conn = Create();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO downloads(id,source,media_id,title,artist,destination_path,status,bytes_downloaded,total_bytes,queued_at,started_at,completed_at,error_message)
VALUES(@id,@src,@mid,@title,@artist,@dest,@status,@bd,@tb,@q,@s,@c,@e)
ON CONFLICT(id) DO UPDATE SET source=excluded.source, media_id=excluded.media_id, title=excluded.title, artist=excluded.artist, destination_path=excluded.destination_path, status=excluded.status, bytes_downloaded=excluded.bytes_downloaded, total_bytes=excluded.total_bytes, queued_at=excluded.queued_at, started_at=excluded.started_at, completed_at=excluded.completed_at, error_message=excluded.error_message;";
            var p = cmd.CreateParameter(); p.ParameterName = "@id"; p.Value = job.Id; cmd.Parameters.Add(p);
            cmd.Parameters.Add(new SqliteParameter("@src", job.MediaItem.Source ?? string.Empty));
            cmd.Parameters.Add(new SqliteParameter("@mid", job.MediaItem.Id ?? string.Empty));
            cmd.Parameters.Add(new SqliteParameter("@title", job.MediaItem.Title ?? string.Empty));
            cmd.Parameters.Add(new SqliteParameter("@artist", job.MediaItem.Artist ?? string.Empty));
            cmd.Parameters.Add(new SqliteParameter("@dest", job.DestinationPath ?? string.Empty));
            cmd.Parameters.Add(new SqliteParameter("@status", job.Status.ToString()));
            cmd.Parameters.Add(new SqliteParameter("@bd", job.BytesDownloaded));
            cmd.Parameters.Add(new SqliteParameter("@tb", job.TotalBytes));
            cmd.Parameters.Add(new SqliteParameter("@q", job.QueuedAt.ToString("O")));
            cmd.Parameters.Add(new SqliteParameter("@s", job.StartedAt?.ToString("O")));
            cmd.Parameters.Add(new SqliteParameter("@c", job.CompletedAt?.ToString("O")));
            cmd.Parameters.Add(new SqliteParameter("@e", job.ErrorMessage ?? string.Empty));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Upsert download failed"); }
    }

    public async Task<List<DownloadJob>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<DownloadJob>();
        try
        {
            await using var conn = Create();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,source,media_id,title,artist,destination_path,status,bytes_downloaded,total_bytes,queued_at,started_at,completed_at,error_message FROM downloads ORDER BY queued_at DESC";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var job = new DownloadJob
                {
                    Id = r.GetString(0),
                    MediaItem = new MediaItem { Source = r.GetString(1), Id = r.GetString(2), Title = r.IsDBNull(3) ? string.Empty : r.GetString(3), Artist = r.IsDBNull(4) ? string.Empty : r.GetString(4) },
                    DestinationPath = r.IsDBNull(5) ? string.Empty : r.GetString(5),
                    Status = Enum.TryParse<DownloadStatus>(r.GetString(6), out var st) ? st : DownloadStatus.Completed,
                    BytesDownloaded = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    TotalBytes = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                    QueuedAt = DateTime.TryParse(r.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind, out var q) ? q : DateTime.UtcNow,
                    StartedAt = r.IsDBNull(10) ? null : DateTime.TryParse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind, out var s) ? s : null,
                    CompletedAt = r.IsDBNull(11) ? null : DateTime.TryParse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind, out var c) ? c : null,
                    ErrorMessage = r.IsDBNull(12) ? null : r.GetString(12)
                };
                list.Add(job);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to load persisted downloads"); }
        return list;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await using var conn = Create();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE id=@id";
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Delete download failed"); }
    }

    public async Task VacuumAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = Create();
            await conn.OpenAsync(ct);
            var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { }
    }
}