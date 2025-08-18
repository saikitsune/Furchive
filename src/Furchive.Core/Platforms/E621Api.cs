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
    private string? _authQuery; // cached query string like login=USER&api_key=KEY
    // In-memory cache for pool details to avoid repeated HTTP calls within a session
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (E621PoolDetail detail, DateTime expires)> _poolCache = new();
    private static readonly SemaphoreSlim _poolFetchLimiter = new(initialCount: 3, maxCount: 3); // bounded concurrency
    private static readonly TimeSpan _poolTtl = TimeSpan.FromMinutes(30);

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
            var healthUrl = AppendAuth("https://e621.net/posts.json?limit=1&tags=solo");
            using var request = new HttpRequestMessage(HttpMethod.Get, healthUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await _httpClient.SendAsync(request);
            
            return new PlatformHealth
            {
                Source = PlatformName,
                IsAvailable = response.IsSuccessStatusCode,
                IsAuthenticated = !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_apiKey),
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
            _apiKey = _apiKey.Trim();
        }
        if (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_apiKey))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_username}:{_apiKey}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            _authQuery = $"login={Uri.EscapeDataString(_username)}&api_key={Uri.EscapeDataString(_apiKey)}";
        }
        else
        {
            _authQuery = null;
        }

    // Report success only when credentials for auth are present, not just UA
    var hasCreds = !string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_apiKey);
    return Task.FromResult(hasCreds);
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
            url = AppendAuthAndFilter(url, isPostsList: true);

            _logger.LogInformation("e621 search: tags=\"{Tags}\", page={Page}, limit={Limit}", tagQuery, parameters.Page, limit);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var httpResp = await _httpClient.SendAsync(req);
            httpResp.EnsureSuccessStatusCode();
            var json = await httpResp.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var data = JsonSerializer.Deserialize<E621PostsResponse>(json, options) ?? new E621PostsResponse();

            // Map posts to items first
            var allItems = data.Posts.Select(MapPostToMediaItem).ToList();
            var rawCount = data.Posts.Count; // Use unfiltered count for pagination determination

            // If not authenticated with API key, drop items that lack a direct file URL (often require auth)
            var items = string.IsNullOrWhiteSpace(_apiKey)
                ? allItems.Where(i => !string.IsNullOrWhiteSpace(i.FullImageUrl)).ToList()
                : allItems;

            // Client-side rating filter for multi-rating selections
            if (selectedRatings.Count > 0 && selectedRatings.Count < 3)
            {
                items = items.Where(i => selectedRatings.Contains(i.Rating)).ToList();
            }

            // Prefetch pool context only for items we keep, using cache + bounded concurrency
            try
            {
                var keepIds = new HashSet<int>(items.Select(i => int.TryParse(i.Id, out var x) ? x : -1).Where(x => x > 0));
                if (keepIds.Count > 0)
                {
                    var postPoolPairs = new List<(int postId, int poolId)>();
                    foreach (var p in data.Posts)
                    {
                        if (!keepIds.Contains(p.Id)) continue;
                        var pools = p.Pools ?? p.Relationships?.Pools;
                        if (pools != null && pools.Count > 0)
                        {
                            postPoolPairs.Add((p.Id, pools[0]));
                        }
                    }
                    var poolIds = postPoolPairs.Select(t => t.poolId).Distinct().ToList();
                    if (poolIds.Count > 0)
                    {
                        var detailsByPool = new Dictionary<int, E621PoolDetail>();
                        // Fetch with bounded parallelism and caching
                        var tasks = poolIds.Select(async pid =>
                        {
                            var det = await GetPoolDetailCachedAsync(pid, options, CancellationToken.None);
                            lock (detailsByPool)
                            {
                                if (det != null) detailsByPool[pid] = det;
                            }
                        });
                        await Task.WhenAll(tasks);

                        if (detailsByPool.Count > 0)
                        {
                            var ctxMap = new Dictionary<int, (int poolId, string name, int page)>();
                            foreach (var kv in detailsByPool)
                            {
                                var pid = kv.Key; var det = kv.Value;
                                var name = det.Name ?? $"Pool {pid}";
                                if (det.PostIds == null) continue;
                                foreach (var pair in postPoolPairs.Where(pp => pp.poolId == pid))
                                {
                                    var idx = det.PostIds.FindIndex(x => x == pair.postId);
                                    if (idx >= 0) ctxMap[pair.postId] = (pid, name, idx + 1);
                                }
                            }
                            if (ctxMap.Count > 0)
                            {
                                foreach (var item in items)
                                {
                                    if (int.TryParse(item.Id, out var iid) && ctxMap.TryGetValue(iid, out var info))
                                    {
                                        item.TagCategories ??= new Dictionary<string, List<string>>();
                                        item.TagCategories["pool_id"] = new List<string> { info.poolId.ToString() };
                                        item.TagCategories["pool_name"] = new List<string> { info.name };
                                        item.TagCategories["page_number"] = new List<string> { info.page.ToString("D5") };
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Prefetching pool context during search failed");
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

    private async Task<E621PoolDetail?> GetPoolDetailCachedAsync(int poolId, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        try
        {
            if (_poolCache.TryGetValue(poolId, out var entry))
            {
                if (DateTime.UtcNow < entry.expires)
                {
                    return entry.detail;
                }
                else
                {
                    _ = _poolCache.TryRemove(poolId, out _);
                }
            }

            await _poolFetchLimiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check after acquiring the limiter
                if (_poolCache.TryGetValue(poolId, out entry) && DateTime.UtcNow < entry.expires)
                    return entry.detail;

                var url = AppendAuth($"https://e621.net/pools/{poolId}.json");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var det = JsonSerializer.Deserialize<E621PoolDetail>(json, jsonOptions);
                if (det != null)
                {
                    _poolCache[poolId] = (det, DateTime.UtcNow.Add(_poolTtl));
                }
                return det;
            }
            finally
            {
                _poolFetchLimiter.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pool detail fetch failed for {PoolId}", poolId);
            return null;
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
            url = AppendAuth(url);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await _httpClient.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var wrapper = JsonSerializer.Deserialize<E621PostWrapper>(json, options);
            var post = wrapper?.Post;
            if (post == null) return null;

            var item = MapPostToMediaItem(post);
            try
            {
                var pools = post.Pools ?? post.Relationships?.Pools;
                if (pools != null && pools.Count > 0)
                {
                    var poolId = pools[0];
                    var det = await GetPoolDetailCachedAsync(poolId, options, CancellationToken.None);
                    if (det != null)
                    {
                        int page = 0;
                        if (det.PostIds != null && int.TryParse(id, out var pid))
                        {
                            var idx = det.PostIds.FindIndex(x => x == pid);
                            if (idx >= 0) page = idx + 1;
                        }
                        item.TagCategories ??= new Dictionary<string, List<string>>();
                        item.TagCategories["pool_id"] = new List<string> { poolId.ToString() };
                        item.TagCategories["pool_name"] = new List<string> { det.Name ?? ($"Pool {poolId}") };
                        if (page > 0)
                            item.TagCategories["page_number"] = new List<string> { page.ToString("D5") };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Enriching media details with pool context failed for {Id}", id);
            }

            return item;
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
            url = AppendAuth(url);
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
                url = AppendAuth(url);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                var pools = JsonSerializer.Deserialize<List<E621Pool>>(json, options) ?? new();
                if (pools.Count == 0) break;
                all.AddRange(pools
                    .Where(p => (p.Name ?? string.Empty).StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) == false && p.PostCount > 0)
                    .Select(p => new PoolInfo { Id = p.Id, Name = p.Name ?? $"Pool {p.Id}", PostCount = p.PostCount }));
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
                url = AppendAuth(url);
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
                all.AddRange(pools
                    .Where(p => (p.Name ?? string.Empty).StartsWith("(deleted)", StringComparison.OrdinalIgnoreCase) == false && p.PostCount > 0)
                    .Select(p => new PoolInfo { Id = p.Id, Name = p.Name ?? $"Pool {p.Id}", PostCount = p.PostCount }));
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

    public async Task<List<PoolInfo>> GetPoolsUpdatedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureUserAgent();
            var updated = new List<PoolInfo>();
            int page = 1;
            const int limit = 320;
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            // Query most recently updated pools first, stop when older than threshold.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = $"https://e621.net/pools.json?limit={limit}&page={page}&search[order]=updated_desc";
                url = AppendAuth(url);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                var resp = await _httpClient.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                var pools = JsonSerializer.Deserialize<List<E621Pool>>(json, options) ?? new();
                if (pools.Count == 0) break;
                // If the last item on this page is older than sinceUtc, we can stop after processing items newer than sinceUtc on this page
                var pageOlder = pools.All(p => (p.UpdatedAt ?? DateTime.MinValue) < sinceUtc);
                foreach (var p in pools)
                {
                    var u = p.UpdatedAt ?? DateTime.MinValue;
                    if (u >= sinceUtc)
                    {
                        updated.Add(new PoolInfo { Id = p.Id, Name = p.Name ?? $"Pool {p.Id}", PostCount = p.PostCount });
                    }
                }
                if (pageOlder || pools.Count < limit) break;
                page++;
                await Task.Delay(150, cancellationToken);
            }
            // Deduplicate by Id and return; order doesn't matter for incremental set
            return updated
                .GroupBy(p => p.Id)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPoolsUpdatedSince failed for e621");
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
            url = AppendAuthAndFilter(url, isPostsList: true);
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
            poolUrl = AppendAuth(poolUrl);
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

                // Fetch posts by explicit IDs in batches using the tags query with order:custom.
                // Danbooru/e621 support "order:custom id:1,2,3" to return exactly those posts in the provided order.
                var result = new List<MediaItem>(ids.Count);
                var idsSet = new HashSet<int>(ids);
                var seen = new HashSet<int>();
                const int batchSize = 100; // conservative; within typical limits
                for (int i = 0; i < ids.Count; i += batchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var slice = ids.Skip(i).Take(batchSize).ToList();
                    var idsParam = string.Join(",", slice);
                    // Use tags query to request these IDs in custom order; set limit to slice count.
                    var tagQuery = $"order:custom id:{idsParam}";
                    var url = $"https://e621.net/posts.json?tags={Uri.EscapeDataString(tagQuery)}&limit={slice.Count}";
                    url = AppendAuthAndFilter(url, isPostsList: true);
                    using var r = new HttpRequestMessage(HttpMethod.Get, url);
                    r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    var pr = await _httpClient.SendAsync(r, cancellationToken);
                    pr.EnsureSuccessStatusCode();
                    var pj = await pr.Content.ReadAsStringAsync(cancellationToken);
                    var data = JsonSerializer.Deserialize<E621PostsResponse>(pj, options) ?? new E621PostsResponse();
                    var mapped = data.Posts.Select(MapPostToMediaItem).ToList();
                    if (string.IsNullOrWhiteSpace(_apiKey))
                        mapped = mapped.Where(m => !string.IsNullOrWhiteSpace(m.FullImageUrl)).ToList();
                    // Defensive: keep only posts whose IDs are in the slice and maintain slice order
                    var orderMap = slice.Select((id, idx) => (id, idx)).ToDictionary(t => t.id, t => t.idx);
                    mapped = mapped
                        .Where(m => int.TryParse(m.Id, out var mid) && orderMap.ContainsKey(mid))
                        .OrderBy(m => orderMap[int.Parse(m.Id)])
                        .ToList();
                    // Deduplicate across batches
                    foreach (var m in mapped)
                    {
                        if (int.TryParse(m.Id, out var mid) && seen.Add(mid))
                            result.Add(m);
                    }
                    await Task.Delay(100, cancellationToken);
                }
                // Final sort by full pool order
                var fullOrder = ids.Select((id, idx) => (id, idx)).ToDictionary(t => t.id, t => t.idx);
                // Build final list strictly in pool order from the deduped results
                var byId = result.Where(m => int.TryParse(m.Id, out _))
                                  .GroupBy(m => int.Parse(m.Id))
                                  .ToDictionary(g => g.Key, g => g.First());
                var ordered = new List<MediaItem>(byId.Count);
                foreach (var id in ids)
                {
                    if (byId.TryGetValue(id, out var item)) ordered.Add(item);
                }
                return ordered;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetAllPoolPosts failed for e621 pool {PoolId}", poolId);
            return new List<MediaItem>();
        }
    }

    public async Task<(int poolId, string poolName, int pageNumber)?> GetPoolContextForPostAsync(string postId, CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureUserAgent();
            // Fetch post details which include pools[] ids
            var url = AppendAuth($"https://e621.net/posts/{postId}.json");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var resp = await _httpClient.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var wrapper = JsonSerializer.Deserialize<E621PostWrapper>(json, options);
            var post = wrapper?.Post;
            if (post == null) return null;

            // e621 post JSON contains pool IDs under relationships or pools (depending on API evolution).
            // We'll make a second request to pool details to compute index when possible.
            var postPools = post.Pools ?? post.Relationships?.Pools;
            if (postPools == null || postPools.Count == 0)
                return null;

            // Prefer the first pool id as primary context
            var poolId = postPools[0];
            // Get pool details to compute the page index of this post inside the pool
            var poolUrl = AppendAuth($"https://e621.net/pools/{poolId}.json");
            using var preq = new HttpRequestMessage(HttpMethod.Get, poolUrl);
            preq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var presp = await _httpClient.SendAsync(preq, cancellationToken);
            presp.EnsureSuccessStatusCode();
            var pjson = await presp.Content.ReadAsStringAsync(cancellationToken);
            var pool = JsonSerializer.Deserialize<E621PoolDetail>(pjson, options);
            if (pool == null) return null;

            var pageNumber = 0;
            if (pool.PostIds != null)
            {
                var pid = int.TryParse(postId, out var parsed) ? parsed : -1;
                var idx = pool.PostIds.FindIndex(x => x == pid);
                if (idx >= 0) pageNumber = idx + 1; // 1-based index
            }

            var name = pool.Name ?? $"Pool {poolId}";
            return (poolId, name, pageNumber);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetPoolContextForPostAsync failed for post {PostId}", postId);
            return null;
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

    private string AppendAuth(string url)
    {
        if (string.IsNullOrWhiteSpace(_authQuery)) return url;
        return url.Contains("?") ? $"{url}&{_authQuery}" : $"{url}?{_authQuery}";
    }

    // When authenticated, disable account-level filters that may hide posts (e.g., safe filter) by setting filter_id=0 on list endpoints
    private string AppendAuthAndFilter(string url, bool isPostsList)
    {
        var u = AppendAuth(url);
        if (isPostsList && !string.IsNullOrWhiteSpace(_apiKey))
        {
            u = u.Contains("?") ? $"{u}&filter_id=0" : $"{u}?filter_id=0";
        }
        return u;
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
        static string NormalizeMediaUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            var u = url.Trim();
            // Handle scheme-relative URLs like //static1.e621.net/...
            if (u.StartsWith("//")) return "https:" + u;
            // Handle common relative paths returned by some clients
            if (u.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
                return "https://static1.e621.net" + u; // media files are hosted on static CDN
            if (u.StartsWith("/posts/", StringComparison.OrdinalIgnoreCase))
                return "https://e621.net" + u; // site page
            // If already absolute, keep it
            if (Uri.TryCreate(u, UriKind.Absolute, out _)) return u;
            // Fallback: try to treat as path under main site
            return "https://e621.net/" + u.TrimStart('/');
        }

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
        // Define tags that must live under meta only (never under artist/general)
        var warningFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sound_warning",
            "third-party_edit",
            "epilepsy_warning",
            // Ensure Conditional_DNP is treated as meta, not artist
            "conditional_dnp"
        };
        if (p.Tags != null)
        {
            // Capture original tags across all categories before any cleaning to ensure we can enforce meta-only flags
            var originalAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void addOriginal(IEnumerable<string>? arr)
            {
                if (arr == null) return;
                foreach (var t in arr) originalAll.Add(t);
            }
            addOriginal(p.Tags.General);
            addOriginal(p.Tags.Species);
            addOriginal(p.Tags.Character);
            addOriginal(p.Tags.Copyright);
            addOriginal(p.Tags.Artist);
            addOriginal(p.Tags.Invalid);
            addOriginal(p.Tags.Lore);
            addOriginal(p.Tags.Meta);

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
                // If they occur anywhere in the ORIGINAL tag sets, make sure they exist under meta
                if (!categories["meta"].Contains(t) && originalAll.Contains(t))
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

        // Build preview and full URLs, normalizing to absolute https
        var previewUrl = NormalizeMediaUrl(p.Preview?.Url);
        if (string.IsNullOrWhiteSpace(previewUrl)) previewUrl = NormalizeMediaUrl(p.Sample?.Url);
        if (string.IsNullOrWhiteSpace(previewUrl)) previewUrl = NormalizeMediaUrl(p.File?.Url);
        var fullUrl = NormalizeMediaUrl(p.File?.Url);

        return new MediaItem
        {
            Id = p.Id.ToString(),
            Source = "e621",
            Title = $"Post {p.Id}",
            Artist = artist,
            PreviewUrl = previewUrl,
            FullImageUrl = fullUrl,
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
    // Some API variants return pools array directly on the post
    [JsonPropertyName("pools")] public List<int>? Pools { get; set; }
    // Some variants nest relationships -> pools
    [JsonPropertyName("relationships")] public E621Relationships? Relationships { get; set; }
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

    private sealed class E621Relationships
    {
        [JsonPropertyName("pools")] public List<int>? Pools { get; set; }
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
    [JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
    }

    private sealed class E621PoolDetail
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("post_ids")] public List<int>? PostIds { get; set; }
    }
}
