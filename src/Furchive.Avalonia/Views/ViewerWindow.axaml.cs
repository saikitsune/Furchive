using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Furchive.Avalonia.Behaviors;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;
    private Control? _webView;

    public ViewerWindow()
    {
        InitializeComponent();
        this.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        this.Opened += async (_, __) => { try { await LoadAsync(); } catch { } };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenOriginal(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m)
        {
            var url = string.IsNullOrWhiteSpace(m.FullImageUrl) ? m.PreviewUrl : m.FullImageUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(url); } catch { }
            }
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.SourceUrl))
        {
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.SourceUrl); } catch { }
        }
    }

    private async Task LoadAsync()
    {
        if (DataContext is not MediaItem m) return;
        var imageBorder = this.FindControl<Border>("ImageContainer");
        var webBorder = this.FindControl<Border>("WebViewContainer");
        var img = this.FindControl<Image>("ImageView");
        if (imageBorder == null || webBorder == null || img == null) return;

        var ext = (!string.IsNullOrWhiteSpace(m.FileExtension) ? m.FileExtension : TryGetExtensionFromUrl(m.FullImageUrl) ?? TryGetExtensionFromUrl(m.PreviewUrl) ?? string.Empty).Trim('.').ToLowerInvariant();
        var isVideo = ext is "mp4" or "webm" or "mkv";
        var looksGif = ext == "gif" || LooksLikeGifFromUrl(m.FullImageUrl) || LooksLikeGifFromUrl(m.PreviewUrl);
        var bestUrl = !string.IsNullOrWhiteSpace(m.FullImageUrl) ? m.FullImageUrl : m.PreviewUrl;

        if (isVideo || looksGif)
        {
            imageBorder.IsVisible = false;
            webBorder.IsVisible = true;
            AttachWebView(bestUrl, isVideo: isVideo, isGif: looksGif);
            return;
        }

        // Try local final file first (static images)
    try
    {
            var settings = App.Services?.GetService<ISettingsService>();
            var baseDir = settings?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            var finalPath = GenerateFinalPath(m, baseDir, settings);
            if (!string.IsNullOrWhiteSpace(finalPath) && File.Exists(finalPath) && IsImageFile(finalPath))
            {
                img.Source = new Bitmap(finalPath);
                imageBorder.IsVisible = true;
                webBorder.IsVisible = false;
                return;
            }
        }
        catch { }

        // Try temp file next
        try
        {
            var temp = GetTempPathFor(m);
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp) && IsImageFile(temp))
            {
                img.Source = new Bitmap(temp);
                imageBorder.IsVisible = true;
                webBorder.IsVisible = false;
                return;
            }
        }
        catch { }

        // Remote best-quality image
        try
        {
            imageBorder.IsVisible = true;
            webBorder.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(bestUrl))
                RemoteImage.SetSourceUri(img, bestUrl);
        }
        catch { }

        await Task.CompletedTask;
    }

    private void AttachWebView(string? url, bool isVideo, bool isGif)
    {
        var host = this.FindControl<Panel>("WebHost");
        var fallback = this.FindControl<Control>("WebFallback");
        var fallbackText = this.FindControl<TextBlock>("WebFallbackText");
        if (host == null) return;

        // Helper: set fallback message safely
        void ShowFallback(string message)
        {
            try { if (fallbackText != null) fallbackText.Text = message; } catch { }
            try { if (fallback != null) fallback.IsVisible = true; } catch { }
        }

        try
        {
            // Check WebView2 runtime presence (best-effort, via reflection)
            try
            {
                var envType = Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core", throwOnError: false)
                             ?? Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment", throwOnError: false);
                var version = envType?.GetMethod("GetAvailableBrowserVersionString", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.Invoke(null, null) as string;
                if (string.IsNullOrWhiteSpace(version))
                {
                    ShowFallback("Microsoft Edge WebView2 Runtime not detected. Install the WebView2 runtime to enable embedded playback, or use Open in browser.");
                    // Continue anyway; some implementations may bootstrap the runtime.
                }
            }
            catch
            {
                // Ignore detection errors; we'll proceed and surface any creation errors below
            }

            // Create WebView instance via reflection only (avoid compile-time dependency), with strict filtering
            Control? created = null;
            Type? webViewType = null;
            string[] candidates = new[]
            {
                "WebView.Avalonia.WebView, WebView.Avalonia",
                "Avalonia.WebView.Controls.WebView, Avalonia.WebView",
                "Avalonia.WebView.AvaloniaWebView, Avalonia.WebView",
            };
            foreach (var c in candidates)
            {
                try
                {
                    var t = Type.GetType(c, throwOnError: false);
                    if (t == null) continue;
                    if (!typeof(Control).IsAssignableFrom(t)) continue;
                    if (t.IsAbstract || t.Name.Contains("Handler", StringComparison.OrdinalIgnoreCase)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                    webViewType = t; break;
                }
                catch { }
            }
            if (webViewType == null)
            {
                // Scan as a last resort but filter aggressively
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types; try { types = asm.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (!typeof(Control).IsAssignableFrom(t)) continue;
                        var fn = t.FullName ?? string.Empty;
                        if (!fn.Contains("WebView", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!(fn.StartsWith("WebView.Avalonia.", StringComparison.Ordinal) || fn.StartsWith("Avalonia.WebView.", StringComparison.Ordinal))) continue;
                        if (t.IsAbstract || t.Name.Contains("Handler", StringComparison.OrdinalIgnoreCase)) continue;
                        if (t.GetConstructor(Type.EmptyTypes) == null) continue;
                        webViewType = t; break;
                    }
                    if (webViewType != null) break;
                }
            }
            if (webViewType == null)
            {
                var loadedNames = string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => !string.IsNullOrWhiteSpace(n)));
                ShowFallback("Embedded WebView control not found. Ensure WebView.Avalonia is restored. Loaded: " + loadedNames);
                return;
            }
            try { created = Activator.CreateInstance(webViewType) as Control; }
            catch (Exception ex)
            {
                ShowFallback("Failed to create WebView instance: " + ex.Message);
                return;
            }
            if (created == null)
            {
                ShowFallback("Failed to create WebView instance (null).");
                return;
            }

            _webView = created;
            host.Children.Clear();
            _webView.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
            _webView.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
            if (_webView is ContentControl cc)
            {
                cc.HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch;
                cc.VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch;
            }
            host.Children.Add(_webView);
            if (fallback != null) fallback.IsVisible = false;

            if (string.IsNullOrWhiteSpace(url)) return;

            // Dispatch navigation depending on available API surface
            var wt = created.GetType();
            var navigateToString = wt.GetMethod("NavigateToString")
                                   ?? wt.GetMethod("NavigateToStringAsync")
                                   ?? wt.GetMethod("LoadHtmlString");
            var navigateMethod = wt.GetMethod("Navigate") ?? wt.GetMethod("GoTo");
            var htmlProp = wt.GetProperty("HtmlContent") ?? wt.GetProperty("Html") ?? wt.GetProperty("ContentHtml");
            var addressProp = wt.GetProperty("Address") ?? wt.GetProperty("Source") ?? wt.GetProperty("Url") ?? wt.GetProperty("Uri");

            object ToTarget(object s)
            {
                if (s is string ss)
                {
                    try { if (addressProp?.PropertyType == typeof(Uri)) return new Uri(ss); } catch { }
                    return ss;
                }
                return s;
            }

            if (isVideo && (navigateToString != null || htmlProp != null))
            {
                var html = $"<html><head><meta http-equiv='X-UA-Compatible' content='IE=Edge'/></head><body style='margin:0;background:#202020;display:flex;align-items:center;justify-content:center;'><video src='{url}' controls autoplay style='max-width:100%;max-height:100%'></video></body></html>";
                try { if (navigateToString != null) navigateToString.Invoke(_webView, new object?[] { html }); else htmlProp!.SetValue(_webView, html); }
                catch (Exception ex) { ShowFallback("WebView navigation failed: " + ex.Message); }
            }
            else if (isGif && (navigateToString != null || htmlProp != null))
            {
                var html = $"<html><head><meta http-equiv='X-UA-Compatible' content='IE=Edge'/></head><body style='margin:0;background:#202020;display:flex;align-items:center;justify-content:center;'><img src='{url}' style='max-width:100%;max-height:100%'/></body></html>";
                try { if (navigateToString != null) navigateToString.Invoke(_webView, new object?[] { html }); else htmlProp!.SetValue(_webView, html); }
                catch (Exception ex) { ShowFallback("WebView navigation failed: " + ex.Message); }
            }
            else if (addressProp != null)
            {
                try { addressProp.SetValue(_webView, ToTarget(url!)); }
                catch (Exception ex) { ShowFallback("WebView address set failed: " + ex.Message); }
            }
            else if (navigateMethod != null)
            {
                try { navigateMethod.Invoke(_webView, new object?[] { ToTarget(url!) }); }
                catch (Exception ex) { ShowFallback("WebView navigate() failed: " + ex.Message); }
            }
            else
            {
                ShowFallback("No suitable navigation API found on WebView control.");
            }
        }
        catch (Exception ex)
        {
            ShowFallback("Unexpected WebView error: " + ex.Message);
        }
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            var slider = this.FindControl<Slider>("ZoomSlider");
            var sv = this.FindControl<ScrollViewer>("ImageScroll");
            var content = this.FindControl<Grid>("ImageContent");
            if (slider == null || sv == null || content == null) return;

            var oldZoom = slider.Value;
            var p = e.GetPosition(content);
            var contentOffsetX = sv.Offset.X / oldZoom;
            var contentOffsetY = sv.Offset.Y / oldZoom;
            var anchorX = contentOffsetX + p.X;
            var anchorY = contentOffsetY + p.Y;
            var delta = e.Delta.Y > 0 ? 0.1 : -0.1;
            var newZoom = Math.Clamp(oldZoom + delta, slider.Minimum, slider.Maximum);
            if (Math.Abs(newZoom - oldZoom) < 0.0001) { e.Handled = true; return; }
            slider.Value = newZoom;
            var newOffsetX = (anchorX - p.X) * newZoom;
            var newOffsetY = (anchorY - p.Y) * newZoom;
            sv.Offset = new Vector(Math.Max(0, newOffsetX), Math.Max(0, newOffsetY));
            e.Handled = true;
        }
        catch { }
    }

    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var props = e.GetCurrentPoint(this).Properties;
            if (!(props.IsLeftButtonPressed || props.IsMiddleButtonPressed)) return;
            var pos = e.GetPosition(this);
            _isPanning = true;
            _panStartPointer = pos;
            var sv = this.FindControl<ScrollViewer>("ImageScroll");
            if (sv != null)
            {
                _panStartOffset = new Vector(sv.Offset.X, sv.Offset.Y);
            }
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        catch { }
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            _isPanning = false;
            if (e.Pointer.Captured == this) e.Pointer.Capture(null);
            e.Handled = true;
        }
        catch { }
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        try
        {
            if (!_isPanning) return;
            var sv = this.FindControl<ScrollViewer>("ImageScroll");
            if (sv == null) return;
            var current = e.GetPosition(this);
            var delta = current - _panStartPointer;
            var targetX = _panStartOffset.X - delta.X;
            var targetY = _panStartOffset.Y - delta.Y;
            sv.Offset = new Vector(Math.Max(0, targetX), Math.Max(0, targetY));
            e.Handled = true;
        }
        catch { }
    }

    private static bool LooksLikeGifFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            var u = new Uri(url);
            var path = u.AbsolutePath.ToLowerInvariant();
            var q = u.Query.ToLowerInvariant();
            return path.EndsWith(".gif") || path.Contains(".gif") || q.Contains("format=gif");
        }
        catch { return false; }
    }

    private static string? TryGetExtensionFromUrl(string? url)
    {
        try { if (string.IsNullOrWhiteSpace(url)) return null; var u = new Uri(url); var e = Path.GetExtension(u.AbsolutePath).Trim('.').ToLowerInvariant(); return string.IsNullOrEmpty(e) ? null : e; }
        catch { return null; }
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
