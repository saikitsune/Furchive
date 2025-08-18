using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Furchive.Avalonia.Behaviors;

public static class RemoteImage
{
    public static readonly AttachedProperty<string?> SourceUriProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>(
            "SourceUri",
            typeof(RemoteImage));

    public static void SetSourceUri(AvaloniaObject element, string? value) => element.SetValue(SourceUriProperty, value);
    public static string? GetSourceUri(AvaloniaObject element) => element.GetValue(SourceUriProperty);

    static RemoteImage()
    {
        SourceUriProperty.Changed.AddClassHandler<Image>((img, args) => OnSourceUriChanged(img, args));
    }

    private static async void OnSourceUriChanged(Image img, AvaloniaPropertyChangedEventArgs e)
    {
        var url = e.NewValue as string;
        await LoadAsync(img, url);
    }

    private static async Task LoadAsync(Image img, string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                await Dispatcher.UIThread.InvokeAsync(() => img.Source = null);
                return;
            }

            // Skip obvious video types
            var lower = url.ToLowerInvariant();
            if (lower.EndsWith(".mp4") || lower.EndsWith(".webm") || lower.EndsWith(".mkv"))
            {
                await Dispatcher.UIThread.InvokeAsync(() => img.Source = null);
                return;
            }

            var cache = App.Services?.GetService<IThumbnailCacheService>();
            if (cache == null)
            {
                // Fallback: nothing we can do without cache service
                await Dispatcher.UIThread.InvokeAsync(() => img.Source = null);
                return;
            }

            var cached = cache.TryGetCachedPath(url);
            if (cached == null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    cached = await cache.GetOrAddAsync(url, cts.Token);
                }
                catch
                {
                    cached = null;
                }
            }

            if (cached != null && System.IO.File.Exists(cached))
            {
                var path = cached;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        img.Source = new Bitmap(path);
                    }
                    catch
                    {
                        img.Source = null;
                    }
                });
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(() => img.Source = null);
            }
        }
        catch
        {
            try { await Dispatcher.UIThread.InvokeAsync(() => img.Source = null); } catch { }
        }
    }
}
