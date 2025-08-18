using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Data.Converters;
using Furchive.Core.Interfaces;

namespace Furchive.Avalonia.Converters;

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

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var url = value as string;
        if (string.IsNullOrWhiteSpace(url)) return null;
        var cache = Cache;
        if (cache == null) return url;
        try
        {
            var cached = cache.TryGetCachedPath(url);
            if (cached != null) return cached;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var ctsBg = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await cache.GetOrAddAsync(url, ctsBg.Token);
                }
                catch { }
            });
            return url;
        }
        catch
        {
            return url;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value?.ToString();
}
