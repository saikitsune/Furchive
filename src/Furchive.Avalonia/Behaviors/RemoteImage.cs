using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Net.Http;

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
                // Ignore empty/cleared URL to avoid nuking an explicitly set local Bitmap
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

            // If still not cached and looks like animated (gif/apng/png) attempt direct download (bypass thumbnail cache limitations)
            if (cached == null)
            {
                var lowerUrl = lower;
                if (lowerUrl.EndsWith(".gif") || lowerUrl.EndsWith(".apng") || lowerUrl.EndsWith(".png"))
                {
                    try
                    {
                        var tmpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "anim-cache");
                        Directory.CreateDirectory(tmpDir);
                        var ext = Path.GetExtension(lowerUrl.Split('?', '#')[0]);
                        if (string.IsNullOrWhiteSpace(ext)) ext = ".gif"; // default
                        var fileName = Guid.NewGuid().ToString("N") + ext;
                        var tmpPath = Path.Combine(tmpDir, fileName);
                        using var http = new HttpClient();
                        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        resp.EnsureSuccessStatusCode();
                        await using (var fs = File.Create(tmpPath))
                        await resp.Content.CopyToAsync(fs);
                        cached = tmpPath;
                    }
                    catch { cached = null; }
                }
            }

            if (cached != null && System.IO.File.Exists(cached))
            {
                var path = cached;
                var lowerPath = path.ToLowerInvariant();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        // Static image only (animation handled elsewhere)
                        img.Source = new Bitmap(path);
                    }
                    catch
                    {
                        try { img.Source = null; } catch { }
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

    // Minimal APNG sniff: look for PNG signature then an 'acTL' chunk before first 'IDAT'.
    // APNG sniff/animated assignment removed (handled by future unified animation service if added)
}
