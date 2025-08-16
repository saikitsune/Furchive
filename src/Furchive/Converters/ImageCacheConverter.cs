using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using Furchive.Core.Interfaces;

namespace Furchive.Converters;

/// <summary>
/// Resolves a remote image URL to a local cached file path using IThumbnailCacheService.
/// If caching fails, falls back to the original URL.
/// </summary>
public class ImageCacheConverter : IValueConverter
{
    private static IThumbnailCacheService? _cache;

    private static IThumbnailCacheService? Cache
    {
        get
        {
            if (_cache != null) return _cache;
            _cache = (App.Services)?.GetService(typeof(IThumbnailCacheService)) as IThumbnailCacheService;
            return _cache;
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
    var url = value as string;
    if (string.IsNullOrWhiteSpace(url)) return System.Windows.Data.Binding.DoNothing;
        var cache = Cache;
    if (cache == null) return url;

        try
        {
            // Don't block the UI thread; if cached, return path immediately, else fall back to URL and warm cache in background.
            var cached = cache.TryGetCachedPath(url);
            if (cached != null) return cached;

            _ = Task.Run(async () =>
            {
                try
                {
                    using var ctsBg = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await cache.GetOrAddAsync(url, ctsBg.Token);
                }
                catch { /* background warm failure ignored */ }
            });
            return url;
        }
        catch
        {
            return url; // fallback to URL
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() ?? string.Empty;
    }
}
