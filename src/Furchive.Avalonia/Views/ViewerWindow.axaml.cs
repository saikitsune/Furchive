using Avalonia.Controls;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Furchive.Core.Interfaces;
using System;
using Avalonia.Input;
using System.Reflection;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    public ViewerWindow()
    {
        InitializeComponent();
        this.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

    // Try to attach WebView at runtime if the assembly is available
    TryAttachWebView();

        // Resolve best-quality source with local-first strategy when opened
        this.Opened += async (_, __) =>
        {
            try { await ResolveBestSourceAsync(); } catch { }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenOriginal(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.FullImageUrl))
        {
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.FullImageUrl); } catch { }
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.SourceUrl))
        {
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.SourceUrl); } catch { }
        }
    }

    private void TryAttachWebView()
    {
        try
        {
            var host = this.FindControl<Panel>("WebHost");
            var fallback = this.FindControl<Control>("WebFallback");
            if (host == null) return;
            // Load Avalonia.WebView types via reflection so build doesn't require the package
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Avalonia.WebView", StringComparison.OrdinalIgnoreCase));
            if (asm == null)
            {
                if (fallback != null) fallback.IsVisible = true;
                return;
            }
            var webViewType = asm.GetType("Avalonia.WebView.WebView2");
            if (webViewType == null)
            {
                if (fallback != null) fallback.IsVisible = true;
                return;
            }
            var web = Activator.CreateInstance(webViewType) as Control;
            if (web == null)
            {
                if (fallback != null) fallback.IsVisible = true;
                return;
            }
            // Set Source property if exists and DataContext has FullImageUrl
            var srcProp = webViewType.GetProperty("Source");
            if (srcProp != null && DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.FullImageUrl))
            {
                srcProp.SetValue(web, m.FullImageUrl);
            }
            host.Children.Clear();
            host.Children.Add(web);
            if (fallback != null) fallback.IsVisible = false;
        }
        catch { }
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            var slider = this.FindControl<Slider>("ZoomSlider");
            if (slider == null) return;
            var delta = e.Delta.Y > 0 ? 0.1 : -0.1;
            var next = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            slider.Value = next;
            e.Handled = true;
        }
        catch { }
    }

    private async Task ResolveBestSourceAsync()
    {
        if (DataContext is not MediaItem m) return;
        var img = this.FindControl<Image>("PreviewImage");
        if (img == null) return;

        var ext = !string.IsNullOrWhiteSpace(m.FileExtension) ? m.FileExtension.ToLowerInvariant() : TryGetExtensionFromUrl(m.FullImageUrl) ?? TryGetExtensionFromUrl(m.PreviewUrl) ?? "";
        bool isVideo = ext is "mp4" or "webm" or "mkv";
        if (isVideo)
        {
            // For videos, we already attach WebView and point it at FullImageUrl; local playback via WebView/file:// is not guaranteed.
            return;
        }

        // Try local final download path first
        try
        {
            var settings = App.Services?.GetService<ISettingsService>();
            var basePath = settings?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"))
                           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            var final = GenerateFinalPath(m, basePath, settings);
            if (!string.IsNullOrWhiteSpace(final) && File.Exists(final) && IsImageFile(final))
            {
                // Stop remote loader and show local full-quality image
                try { Furchive.Avalonia.Behaviors.RemoteImage.SetSourceUri(img, null); } catch { }
                try { img.Source = new Bitmap(final); return; } catch { }
            }
        }
        catch { }

        // Try temp path next
        try
        {
            var temp = GetTempPathFor(m);
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp) && IsImageFile(temp))
            {
                try { Furchive.Avalonia.Behaviors.RemoteImage.SetSourceUri(img, null); } catch { }
                try { img.Source = new Bitmap(temp); return; } catch { }
            }
        }
        catch { }

        // Fall back to remote best-quality: prefer FullImageUrl over PreviewUrl for images
        try
        {
            if (!string.IsNullOrWhiteSpace(m.FullImageUrl))
            {
                Furchive.Avalonia.Behaviors.RemoteImage.SetSourceUri(img, m.FullImageUrl);
            }
        }
        catch { }

        await Task.CompletedTask;
    }

    private static bool IsImageFile(string path)
    {
        try
        {
            var e = Path.GetExtension(path).Trim('.').ToLowerInvariant();
            return e is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "tiff";
        }
        catch { return false; }
    }

    private static string? TryGetExtensionFromUrl(string? url)
    {
        try { if (string.IsNullOrWhiteSpace(url)) return null; var u = new Uri(url); var e = Path.GetExtension(u.AbsolutePath).Trim('.').ToLowerInvariant(); return string.IsNullOrEmpty(e) ? null : e; }
        catch { return null; }
    }

    private static string GetTempPathFor(MediaItem item)
    {
        var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
        var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? (TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin") : item.FileExtension;
        string Sanitize(string s) { var invalid = Path.GetInvalidFileNameChars(); var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()); return clean.Replace(" ", "_"); }
        var safeArtist = Sanitize(item.Artist ?? string.Empty);
        var safeTitle = Sanitize(item.Title ?? string.Empty);
        var file = $"{item.Source}_{item.Id}_{safeArtist}_{safeTitle}.{ext}";
        return Path.Combine(tempDir, file);
    }

    private static string GenerateFinalPath(MediaItem mediaItem, string basePath, ISettingsService? settings)
    {
        bool hasPool = mediaItem.TagCategories != null && (mediaItem.TagCategories.ContainsKey("page_number") || mediaItem.TagCategories.ContainsKey("pool_name"));
        var template = hasPool
            ? (settings?.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
            : (settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
        string Sanitize(string s) { var invalid = Path.GetInvalidFileNameChars(); var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray()); return clean.Replace(" ", "_"); }
        var extFinal = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? (TryGetExtensionFromUrl(mediaItem.FullImageUrl) ?? "bin") : mediaItem.FileExtension;
        var rel = template
            .Replace("{source}", mediaItem.Source)
            .Replace("{artist}", Sanitize(mediaItem.Artist ?? string.Empty))
            .Replace("{id}", mediaItem.Id)
            .Replace("{safeTitle}", Sanitize(mediaItem.Title ?? string.Empty))
            .Replace("{ext}", extFinal)
            .Replace("{pool_name}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : string.Empty))
            .Replace("{page_number}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty));
        return Path.Combine(basePath, rel);
    }
}
