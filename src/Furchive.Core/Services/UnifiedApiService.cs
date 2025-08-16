using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Furchive.Core.Services;

/// <summary>
/// Unified API service that aggregates multiple platform APIs
/// </summary>
public class UnifiedApiService : IUnifiedApiService
{
    private readonly Dictionary<string, IPlatformApi> _platforms = new();
    private readonly ILogger<UnifiedApiService> _logger;

    public UnifiedApiService(ILogger<UnifiedApiService> logger)
    {
        _logger = logger;
    }

    public void RegisterPlatform(IPlatformApi platformApi)
    {
        _platforms[platformApi.PlatformName] = platformApi;
        _logger.LogInformation("Registered platform: {Platform}", platformApi.PlatformName);
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
}
