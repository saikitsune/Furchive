using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Furchive.Core.Platforms;

/// <summary>
/// e621 API implementation
/// </summary>
public class E621Api : IPlatformApi
{
    public string PlatformName => "e621";
    
    private readonly HttpClient _httpClient;
    private readonly ILogger<E621Api> _logger;
    private string? _userAgent;
    private string? _username;
    private string? _apiKey;

    public E621Api(HttpClient httpClient, ILogger<E621Api> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PlatformHealth> GetHealthAsync()
    {
        try
        {
            // Test basic connectivity
            // e621 requires a valid User-Agent; ensure one is present even if not yet authenticated
        if (string.IsNullOrWhiteSpace(_userAgent))
            {
                try
                {
            _httpClient.DefaultRequestHeaders.UserAgent.Clear();
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
            var username = Environment.UserName;
            var defaultUa = $"Furchive/{version} (by {username})";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(defaultUa);
                }
                catch { /* ignore */ }
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://e621.net/posts.json?limit=1&tags=solo");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _httpClient.SendAsync(request);
            
            return new PlatformHealth
            {
                Source = PlatformName,
                IsAvailable = response.IsSuccessStatusCode,
                IsAuthenticated = !string.IsNullOrWhiteSpace(_userAgent) && !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_apiKey) ? true : !string.IsNullOrWhiteSpace(_userAgent),
                RateLimitRemaining = GetRateLimitFromHeaders(response.Headers),
                LastChecked = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed for {Platform}", PlatformName);
            return new PlatformHealth
            {
                Source = PlatformName,
                IsAvailable = false,
                LastError = ex.Message,
                LastChecked = DateTime.UtcNow
            };
        }
    }

    public Task<bool> AuthenticateAsync(Dictionary<string, string> credentials)
    {
        credentials.TryGetValue("UserAgent", out var userAgent);
        _userAgent = userAgent;
        if (!string.IsNullOrWhiteSpace(_userAgent))
        {
            try
            {
                // Clear and set UA
                _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set User-Agent header");
            }
        }

        // Optional basic auth with username + api key
        credentials.TryGetValue("Username", out _username);
        credentials.TryGetValue("ApiKey", out _apiKey);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            _apiKey = _apiKey.Replace(" ", string.Empty).Trim();
        }
        if (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_apiKey))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_apiKey}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        return Task.FromResult(!string.IsNullOrWhiteSpace(_userAgent));
    }

    public async Task<SearchResult> SearchAsync(SearchParameters parameters)
    {
        try
        {
            // Ensure UA header is present if not authenticated yet
            if (string.IsNullOrWhiteSpace(_userAgent))
            {
                try
                {
                    _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                    var defaultUa = $"Furchive/{version} (by USERNAME)";
                    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(defaultUa);
                }
                catch { }
            }
            var tags = string.Join(" ", parameters.IncludeTags);
            var excludeTags = string.Join(" ", parameters.ExcludeTags.Select(tag => $"-{tag}"));
            var allTags = $"{tags} {excludeTags}".Trim();

            // e621 treats multiple tags as AND, so multiple rating:* terms yield no results.
            // If exactly one rating is selected, send it to the API. If multiple ratings are selected, omit rating terms and filter client-side.
            var selectedRatings = parameters.Ratings?.Distinct().ToList() ?? new List<ContentRating>();
            var ratingTag = selectedRatings.Count == 1
                ? (selectedRatings[0] switch
                {
                    ContentRating.Safe => "rating:s",
                    ContentRating.Questionable => "rating:q",
                    ContentRating.Explicit => "rating:e",
                    _ => string.Empty
                })
                : string.Empty;

            var artistTag = !string.IsNullOrWhiteSpace(parameters.Artist) ? $"artist:{parameters.Artist}" : string.Empty;
            var sortTag = parameters.Sort switch
            {
                SortOrder.Newest => "order:id_desc",   // newest first by id
                SortOrder.Oldest => "order:id_asc",    // oldest first by id
                SortOrder.Score => "order:score",      // score desc by default
                SortOrder.Favorites => "order:favcount",// favs desc by default
                SortOrder.Random => "order:random",
                _ => "order:id_desc"
            };

            var tagQuery = string.Join(" ", new[] { allTags, ratingTag, artistTag, sortTag }.Where(s => !string.IsNullOrWhiteSpace(s)));
            // Keep per-request modest to reduce UI/network spikes
            var limit = Math.Clamp(parameters.Limit, 1, 100);
            var url = $"https://e621.net/posts.json?tags={Uri.EscapeDataString(tagQuery)}&limit={limit}&page={parameters.Page}";

            _logger.LogInformation("e621 search: tags=\"{Tags}\", page={Page}, limit={Limit}", tagQuery, parameters.Page, limit);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var httpResp = await _httpClient.SendAsync(req);
            httpResp.EnsureSuccessStatusCode();
            var json = await httpResp.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<E621PostsResponse>(json, options) ?? new E621PostsResponse();

            var items = data.Posts.Select(MapPostToMediaItem).ToList();
            var rawCount = data.Posts.Count; // Use unfiltered count for pagination determination
            // If not authenticated with API key, drop items that lack a direct file URL (often require auth)
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                items = items.Where(i => !string.IsNullOrWhiteSpace(i.FullImageUrl)).ToList();
            }
            // Client-side rating filter for multi-rating selections
            if (selectedRatings.Count > 0 && selectedRatings.Count < 3)
            {
                items = items.Where(i => selectedRatings.Contains(i.Rating)).ToList();
            }
            _logger.LogInformation("e621 search returned {Count} posts", items.Count);

            return new SearchResult
            {
                Items = items,
                CurrentPage = parameters.Page,
                // Determine next page based on server page size, not client-side filtering
                HasNextPage = rawCount >= limit,
                TotalCount = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for {Platform}", PlatformName);
            return new SearchResult
            {
                Errors = { [PlatformName] = ex.Message }
            };
        }
    }

    public async Task<MediaItem?> GetMediaDetailsAsync(string id)
    {
        try
        {
            // Ensure UA header is present if not authenticated yet
            if (string.IsNullOrWhiteSpace(_userAgent))
            {
                try
                {
                    _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                    var defaultUa = $"Furchive/{version} (by USERNAME)";
                    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(defaultUa);
                }
                catch { }
            }
            var url = $"https://e621.net/posts/{id}.json";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await _httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var wrapper = JsonSerializer.Deserialize<E621PostWrapper>(json, options);
            var post = wrapper?.Post;
            if (post == null) return null;

            return MapPostToMediaItem(post);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get media details for {Platform} ID {Id}", PlatformName, id);
            return null;
        }
    }

    public async Task<List<TagSuggestion>> GetTagSuggestionsAsync(string query, int limit = 10)
    {
        try
        {
            // Ensure UA header is present if not authenticated yet
            if (string.IsNullOrWhiteSpace(_userAgent))
            {
                try
                {
                    _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                    var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                    var defaultUa = $"Furchive/{version} (by USERNAME)";
                    _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(defaultUa);
                }
                catch { }
            }
            var url = $"https://e621.net/tags.json?search[name_matches]={Uri.EscapeDataString(query)}*&limit={limit}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await _httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var tags = JsonSerializer.Deserialize<List<E621Tag>>(json, options) ?? new();

            return tags.Select(t => new TagSuggestion
            {
                Tag = t.Name ?? string.Empty,
                PostCount = t.PostCount,
                Category = MapE621TagCategory(t.Category)
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tag suggestions failed for {Platform}", PlatformName);
            return new List<TagSuggestion>();
        }
    }

    public async Task<string?> GetDownloadUrlAsync(string id)
    {
        var mediaItem = await GetMediaDetailsAsync(id);
        return mediaItem?.FullImageUrl;
    }

    public async Task<List<PoolInfo>> GetPoolsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureUserAgent();
            // e621 pools API: https://e621.net/pools.json
            // Fetch all pages once per refresh (24h cache on caller)
            var all = new List<PoolInfo>();
            int page = 1;
            const int limit = 320; // e621 supports up to 320 per page
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            int current = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"https://e621.net/pools.json?limit={limit}&page={page}&search[order]=name";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                var pools = JsonSerializer.Deserialize<List<E621Pool>>(json, options) ?? new();
                if (pools.Count == 0) break;
                all.AddRange(pools.Select(p => new PoolInfo { Id = p.Id, Name = p.Name ?? $"Pool {p.Id}", PostCount = p.PostCount }));
                current = all.Count;
                if (pools.Count < limit) break;
                page++;
                // Be gentle with API: slight delay
                await Task.Delay(150, cancellationToken);
            }
            return all.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPools failed for e621");
            return new List<PoolInfo>();
        }
    }

    public async Task<List<PoolInfo>> GetPoolsAsync(IProgress<(int current, int? total)>? progress, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureUserAgent();
            var all = new List<PoolInfo>();
            int page = 1;
            const int limit = 320;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            int? total = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"https://e621.net/pools.json?limit={limit}&page={page}&search[order]=name";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                if (total == null)
                {
                    if (resp.Headers.TryGetValues("X-Total-Count", out var tv))
                    {
                        var s = tv.FirstOrDefault();
                        if (int.TryParse(s, out var t)) total = t;
                    }
                    else if (resp.Headers.TryGetValues("X-Total", out var tv2))
                    {
                        var s2 = tv2.FirstOrDefault();
                        if (int.TryParse(s2, out var t2)) total = t2;
                    }
                }
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                var pools = JsonSerializer.Deserialize<List<E621Pool>>(json, options) ?? new();
                if (pools.Count == 0) break;
                all.AddRange(pools.Select(p => new PoolInfo { Id = p.Id, Name = p.Name ?? $"Pool {p.Id}", PostCount = p.PostCount }));
                progress?.Report((all.Count, total));
                if (pools.Count < limit) break;
                page++;
                await Task.Delay(150, cancellationToken);
            }
            var ordered = all.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            progress?.Report((ordered.Count, total ?? ordered.Count));
            return ordered;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPools (progress) failed for e621");
            return new List<PoolInfo>();
        }
    }

    public async Task<SearchResult> GetPoolPostsAsync(int poolId, int page = 1, int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureUserAgent();
            // According to e621, you can query posts by pool: pool:ID
            // Preserve order in the pool by using order:pool (id asc within pool). We'll paginate client-side via page.
            var offset = (Math.Max(1, page) - 1) * Math.Clamp(limit, 1, 100);
            var perPage = Math.Clamp(limit, 1, 100);
            var tagQuery = $"pool:{poolId} order:pool"; // order in pool sequence
            var url = $"https://e621.net/posts.json?tags={Uri.EscapeDataString(tagQuery)}&limit={perPage}&page={(offset / perPage) + 1}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await _httpClient.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<E621PostsResponse>(json, options) ?? new();

            var items = data.Posts.Select(MapPostToMediaItem).ToList();
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                items = items.Where(i => !string.IsNullOrWhiteSpace(i.FullImageUrl)).ToList();
            }

            return new SearchResult
            {
                Items = items,
                CurrentPage = page,
                HasNextPage = data.Posts.Count >= perPage,
                TotalCount = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPoolPosts failed for e621 pool {PoolId}", poolId);
            return new SearchResult { Errors = { [PlatformName] = ex.Message } };
        }
    }

    public async Task<List<MediaItem>> GetAllPoolPostsAsync(int poolId, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureUserAgent();
            // First, fetch the pool metadata to get ordered post IDs
            var poolUrl = $"https://e621.net/pools/{poolId}.json";
            using (var req = new HttpRequestMessage(HttpMethod.Get, poolUrl))
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var pool = JsonSerializer.Deserialize<E621PoolDetail>(json, options) ?? new E621PoolDetail();
                var ids = pool.PostIds ?? new List<int>();
                if (ids.Count == 0) return new List<MediaItem>();

                // e621 posts.json can accept multiple id: terms. Batch to respect URL length.
                var result = new List<MediaItem>(ids.Count);
                const int batchSize = 100; // conservative
                for (int i = 0; i < ids.Count; i += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slice = ids.Skip(i).Take(batchSize).ToList();
                    var tags = string.Join(" ", slice.Select(id => $"id:{id}"));
                    var url = $"https://e621.net/posts.json?tags={Uri.EscapeDataString(tags)}&limit={slice.Count}";
                    using var r = new HttpRequestMessage(HttpMethod.Get, url);
                    r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    var pr = await _httpClient.SendAsync(r, cancellationToken);
                    pr.EnsureSuccessStatusCode();
                    var pj = await pr.Content.ReadAsStringAsync(cancellationToken);
                    var data = JsonSerializer.Deserialize<E621PostsResponse>(pj, options) ?? new E621PostsResponse();
                    var mapped = data.Posts.Select(MapPostToMediaItem).ToList();
                    if (string.IsNullOrWhiteSpace(_apiKey))
                        mapped = mapped.Where(m => !string.IsNullOrWhiteSpace(m.FullImageUrl)).ToList();
                    // Preserve pool order for this batch
                    var orderMap = slice.Select((id, idx) => (id, idx)).ToDictionary(t => t.id, t => t.idx);
                    mapped.Sort((a, b) => orderMap[int.Parse(a.Id)].CompareTo(orderMap[int.Parse(b.Id)]));
                    result.AddRange(mapped);
                    await Task.Delay(100, cancellationToken);
                }
                // Final sort by full pool order
                var fullOrder = ids.Select((id, idx) => (id, idx)).ToDictionary(t => t.id, t => t.idx);
                result.Sort((a, b) => fullOrder[int.Parse(a.Id)].CompareTo(fullOrder[int.Parse(b.Id)]));
                return result;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllPoolPosts failed for e621 pool {PoolId}", poolId);
            return new List<MediaItem>();
        }
    }

    private void EnsureUserAgent()
    {
        if (string.IsNullOrWhiteSpace(_userAgent))
        {
            try
            {
                _httpClient.DefaultRequestHeaders.UserAgent.Clear();
                var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
                var defaultUa = $"Furchive/{version} (by USERNAME)";
                _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(defaultUa);
            }
            catch { }
        }
    }

    private int GetRateLimitFromHeaders(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("X-RateLimit-Remaining", out var values))
        {
            var v = values.FirstOrDefault();
            if (int.TryParse(v, out var remaining)) return remaining;
        }
        return 0;
    }

    private static string MapE621TagCategory(int category) => category switch
    {
        1 => "artist",
        2 => "copyright",
        3 => "character",
        4 => "species",
        5 => "invalid",
        6 => "meta",
        7 => "lore",
        _ => "general"
    };

    private static MediaItem MapPostToMediaItem(E621Post p)
    {
        var tags = new List<string>();
        var categories = new Dictionary<string, List<string>>
        {
            ["artist"] = new(),
            ["copyright"] = new(),
            ["character"] = new(),
            ["species"] = new(),
            ["general"] = new(),
            ["meta"] = new(),
            ["invalid"] = new(),
            ["lore"] = new()
        };
        // Define warning flags once to reuse for artist filtering and meta-only enforcement
        var warningFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sound_warning", "third-party_edit", "epilepsy_warning" };
        if (p.Tags != null)
        {
            void addTo(string key, IEnumerable<string>? arr)
            {
                if (arr == null) return;
                categories[key].AddRange(arr);
                tags.AddRange(arr);
            }
            addTo("general", p.Tags.General);
            addTo("species", p.Tags.Species);
            addTo("character", p.Tags.Character);
            addTo("copyright", p.Tags.Copyright);
            // Filter out warning flags from artist category; enforce meta-only
            var cleanedArtists = (p.Tags.Artist ?? new List<string>()).Where(a => !warningFlags.Contains(a)).ToList();
            addTo("artist", cleanedArtists);
            addTo("invalid", p.Tags.Invalid);
            addTo("lore", p.Tags.Lore);
            addTo("meta", p.Tags.Meta);
            // Ensure specific warnings appear ONLY under meta
            foreach (var t in warningFlags)
            {
                // Remove from all other categories
                foreach (var key in categories.Keys.ToList())
                {
                    if (!string.Equals(key, "meta", StringComparison.OrdinalIgnoreCase))
                        categories[key].RemoveAll(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase));
                }
                // If they occur anywhere, make sure they exist under meta
                if (!categories["meta"].Contains(t) && tags.Contains(t))
                    categories["meta"].Add(t);
            }
        }
        // Choose first artist ignoring warning flags
        var artist = (p.Tags?.Artist ?? new List<string>()).FirstOrDefault(a => !warningFlags.Contains(a)) ?? string.Empty;
        var rating = (p.Rating ?? "s").ToLowerInvariant() switch
        {
            "s" => ContentRating.Safe,
            "q" => ContentRating.Questionable,
            "e" => ContentRating.Explicit,
            _ => ContentRating.Safe
        };

    return new MediaItem
        {
            Id = p.Id.ToString(),
            Source = "e621",
            Title = $"Post {p.Id}",
            Artist = artist,
            PreviewUrl = p.Preview?.Url ?? p.Sample?.Url ?? p.File?.Url ?? string.Empty,
            FullImageUrl = p.File?.Url ?? string.Empty,
            SourceUrl = $"https://e621.net/posts/{p.Id}",
            TagCategories = categories,
            Rating = rating,
            CreatedAt = p.CreatedAt ?? DateTime.MinValue,
            Score = p.Score?.Total ?? 0,
            FavoriteCount = p.FavCount,
            FileExtension = p.File?.Ext ?? string.Empty,
            FileSizeBytes = p.File?.Size ?? 0,
            Width = p.File?.Width ?? 0,
            Height = p.File?.Height ?? 0
        };
    }

    // e621 JSON DTOs
    private sealed class E621PostsResponse
    {
        [JsonPropertyName("posts")] public List<E621Post> Posts { get; set; } = new();
    }

    private sealed class E621PostWrapper
    {
        [JsonPropertyName("post")] public E621Post? Post { get; set; }
    }

    private sealed class E621Post
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("rating")] public string? Rating { get; set; }
        [JsonPropertyName("tags")] public E621Tags? Tags { get; set; }
        [JsonPropertyName("file")] public E621File? File { get; set; }
        [JsonPropertyName("preview")] public E621Preview? Preview { get; set; }
        [JsonPropertyName("sample")] public E621Sample? Sample { get; set; }
        [JsonPropertyName("fav_count")] public int FavCount { get; set; }
        [JsonPropertyName("score")] public E621Score? Score { get; set; }
    }

    private sealed class E621File
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("ext")] public string? Ext { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("width")] public int? Width { get; set; }
        [JsonPropertyName("height")] public int? Height { get; set; }
    }

    private sealed class E621Preview
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class E621Sample
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class E621Score
    {
        [JsonPropertyName("total")] public int Total { get; set; }
    }

    private sealed class E621Tags
    {
        [JsonPropertyName("general")] public List<string>? General { get; set; }
        [JsonPropertyName("species")] public List<string>? Species { get; set; }
        [JsonPropertyName("character")] public List<string>? Character { get; set; }
        [JsonPropertyName("copyright")] public List<string>? Copyright { get; set; }
        [JsonPropertyName("artist")] public List<string>? Artist { get; set; }
        [JsonPropertyName("invalid")] public List<string>? Invalid { get; set; }
        [JsonPropertyName("lore")] public List<string>? Lore { get; set; }
        [JsonPropertyName("meta")] public List<string>? Meta { get; set; }
    }

    private sealed class E621Tag
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("post_count")] public int PostCount { get; set; }
        [JsonPropertyName("category")] public int Category { get; set; }
    }

    private sealed class E621Pool
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("post_count")] public int PostCount { get; set; }
    }

    private sealed class E621PoolDetail
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("post_ids")] public List<int>? PostIds { get; set; }
    }
}
