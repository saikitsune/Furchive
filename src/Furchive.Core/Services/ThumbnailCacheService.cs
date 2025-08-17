using Furchive.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Furchive.Core.Services;

public class ThumbnailCacheService : IThumbnailCacheService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ThumbnailCacheService> _logger;
    private readonly string _cacheRoot;

    public ThumbnailCacheService(HttpClient httpClient, ILogger<ThumbnailCacheService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "thumb-cache");
        Directory.CreateDirectory(root);
        _cacheRoot = root;
    }

    public string GetCachePath() => _cacheRoot;

    public long GetUsedBytes()
    {
        try
        {
            return Directory.EnumerateFiles(_cacheRoot, "*", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f).Length).Sum();
        }
        catch
        {
            return 0;
        }
    }

    public async Task<string> GetOrAddAsync(string thumbnailUrl, CancellationToken cancellationToken = default)
    {
    if (string.IsNullOrWhiteSpace(thumbnailUrl)) throw new ArgumentException("Invalid thumbnail URL", nameof(thumbnailUrl));
    var url = NormalizeUrl(thumbnailUrl);

    var fileName = HashToFileName(thumbnailUrl) + ".img";
        var path = Path.Combine(_cacheRoot, fileName);
        if (File.Exists(path)) return path;

        try
        {
            // Set minimal headers (best effort)
            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Furchive/1.0 (thumbnail)");
                try { _httpClient.DefaultRequestHeaders.Referrer = new Uri("https://e621.net/"); } catch { }
            }
            catch { }
            using var resp = await _httpClient.GetAsync(url, cancellationToken);
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await resp.Content.CopyToAsync(fs, cancellationToken);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache thumbnail {Url}", thumbnailUrl);
            throw;
        }
    }

    private static string NormalizeUrl(string input)
    {
        var u = input.Trim();
        if (u.StartsWith("//")) return "https:" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out _)) return u;
        if (u.StartsWith("/data/", StringComparison.OrdinalIgnoreCase)) return "https://static1.e621.net" + u;
        if (u.StartsWith("/", StringComparison.OrdinalIgnoreCase)) return "https://e621.net" + u;
        return "https://e621.net/" + u.TrimStart('/');
    }

    public string? TryGetCachedPath(string thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl)) return null;
        var fileName = HashToFileName(thumbnailUrl) + ".img";
        var path = Path.Combine(_cacheRoot, fileName);
        return File.Exists(path) ? path : null;
    }

    public async Task ClearAsync()
    {
        try
        {
            if (!Directory.Exists(_cacheRoot)) return;
            var files = Directory.GetFiles(_cacheRoot);
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* ignore */ }
            }
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear thumbnail cache");
            throw;
        }
    }

    private static string HashToFileName(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
