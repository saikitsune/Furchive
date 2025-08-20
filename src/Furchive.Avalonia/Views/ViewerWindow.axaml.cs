using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Furchive.Avalonia.Behaviors;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using AnimatedImage.Avalonia;
using Furchive.Avalonia.Services;
// WebView backend (HTML5) guarded by HAS_WEBVIEW_AVALONIA define (community WebView.Avalonia)
#if HAS_WEBVIEW_AVALONIA
// We avoid a hard reference to any specific WebView control type to support both
// community and official packages; control is created via reflection at runtime.
#endif

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    // Deprecated WebView fields removed; LibVLC removed; using WebView (if available) and AnimatedImage instead
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;
    private bool _isSeeking;
    private string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs", "viewer.log");

    public ViewerWindow()
    {
        // Create log before any XAML is loaded, so crashes in XAML still produce a log
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Truncate logs at each viewer open for clean test runs
            File.WriteAllText(_logPath, string.Empty);
        }
        catch { }
        SafeLog("ViewerWindow ctor begin");
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            SafeLog("InitializeComponent failed: " + ex.ToString());
            throw;
        }
        this.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        // Record that the viewer was constructed successfully
        SafeLog("ViewerWindow constructed");
            this.Opened += async (_, __) => { SafeLog("ViewerWindow opened"); try { await LoadAsync(); } catch (Exception ex) { SafeLog("LoadAsync crash: " + ex.ToString()); } };
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
		SafeLog("LoadAsync start");
		if (DataContext is not MediaItem m) { SafeLog("No MediaItem DataContext"); return; }
		var imageBorder = this.FindControl<Border>("ImageContainer");
		var videoBorder = this.FindControl<Border>("VideoContainer");
		var img = this.FindControl<Image>("ImageView");
		var gif = this.FindControl<Image>("GifView");
		var videoHost = this.FindControl<ContentControl>("VideoHost");

		if (imageBorder == null || videoBorder == null || img == null || gif == null || videoHost == null) 
		{ 
			SafeLog("Required image/video containers missing"); 
			return; 
		}

		var ext = (!string.IsNullOrWhiteSpace(m.FileExtension) ? m.FileExtension : TryGetExtensionFromUrl(m.FullImageUrl) ?? TryGetExtensionFromUrl(m.PreviewUrl) ?? string.Empty).Trim('.').ToLowerInvariant();
		var isVideo = ext is "mp4" or "webm" or "mkv";
		var looksGif = ext == "gif" || LooksLikeGifFromUrl(m.FullImageUrl) || LooksLikeGifFromUrl(m.PreviewUrl);
		var bestUrl = !string.IsNullOrWhiteSpace(m.FullImageUrl) ? m.FullImageUrl : m.PreviewUrl;
		SafeLog($"Media detect: ext={ext}, isVideo={isVideo}, looksGif={looksGif}, hasUrl={!string.IsNullOrWhiteSpace(bestUrl)}");

        if (isVideo)
        {
            videoBorder.IsVisible = true;
#if HAS_WEBVIEW_AVALONIA
            try
            {
                var proxy = App.Services?.GetService<ILocalMediaProxy>();
                var pageUrl = proxy?.BaseAddress != null && !string.IsNullOrWhiteSpace(bestUrl) ? proxy.GetPlayerUrl(bestUrl!) : bestUrl;
                if (!string.IsNullOrWhiteSpace(pageUrl))
                {
                    SafeLog($"Using WebView backend: {pageUrl}");
                    var webView = CreateWebViewControl();
                    if (webView != null)
                    {
                        videoHost.Content = webView;
                        TrySetWebViewAddress(webView, pageUrl!);
                        return;
                    }
                    else
                    {
                        SafeLog("WebView control type not found at runtime.");
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog("WebView backend failed: " + ex.ToString());
            }
#endif
            // If WebView is not available, show a simple message in place of the video
            videoHost.Content = new TextBlock { Text = "Video playback requires WebView; please enable it in settings.", Foreground = Brushes.White, Margin = new Thickness(12) };
            return;
        }
		
		if (looksGif)
		{
			// GIF playback via AnimatedImage
			videoBorder.IsVisible = false;
			imageBorder.IsVisible = true;
			img.IsVisible = false;
			gif.IsVisible = true;
			if (!string.IsNullOrWhiteSpace(bestUrl))
			{
				try { SetGifSource(gif, bestUrl); } catch { }
			}
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
                videoBorder.IsVisible = false;
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
                videoBorder.IsVisible = false;
                return;
            }
        }
        catch { }

        // Remote best-quality image
        try
        {
            imageBorder.IsVisible = true;
            videoBorder.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(bestUrl))
                RemoteImage.SetSourceUri(img, bestUrl);
        }
        catch { }

        await Task.CompletedTask;
    }

    // LibVLC playback removed; video control handlers below are no-ops with WebView backend

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(ms);
		return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void OnPlayPause(object? sender, RoutedEventArgs e) { /* no-op without LibVLC */ }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    { _isSeeking = true; }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
    try { /* no-op without LibVLC */ }
        finally { _isSeeking = false; }
    }

    private void OnSeekValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSeeking)
        {
            try
            {
                var cur = this.FindControl<TextBlock>("CurrentTimeText");
                if (cur != null)
                    cur.Text = FormatTime((long)e.NewValue);
            }
            catch { }
        }
    }

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e) { /* no-op without LibVLC */ }

#if HAS_WEBVIEW_AVALONIA
    private static Control? CreateWebViewControl()
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            // Prefer types named exactly "WebView" that derive from Avalonia.Controls.Control
            var candidate = assemblies
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .FirstOrDefault(t => t.Name == "WebView" && typeof(Control).IsAssignableFrom(t));
            if (candidate != null)
            {
                return Activator.CreateInstance(candidate) as Control;
            }
        }
        catch { }
        return null;
    }

    private void TrySetWebViewAddress(Control webView, string url)
    {
        try
        {
            var t = webView.GetType();
            // Try common property names used by different WebView packages
            var prop = t.GetProperty("Address") ?? t.GetProperty("Source") ?? t.GetProperty("Url");
            if (prop != null && prop.CanWrite)
            {
                if (prop.PropertyType == typeof(Uri))
                {
                    prop.SetValue(webView, new Uri(url));
                }
                else
                {
                    prop.SetValue(webView, url);
                }
            }
            else
            {
                SafeLog("No suitable property (Address/Source/Url) found on WebView instance");
            }
        }
        catch (Exception ex)
        {
            SafeLog("TrySetWebViewAddress failed: " + ex.ToString());
        }
    }
#endif

    private static readonly HttpClient s_http = new HttpClient();
    private static void SetGifSource(Image gifControl, string url)
    {
        // AnimatedImage.Avalonia v2 uses an attached property on Image: anim:ImageBehavior.AnimatedSource
        // We can't reference the assembly types directly here; set via reflection on attached property.
        try
        {
            var uri = new Uri(url);
            // Log intent lightly by toggling a tag (no direct logging method here)
            // If remote, download to local cache then animate from file to ensure decoder can access it
            if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "cache", "gifs");
                        Directory.CreateDirectory(cacheDir);
                        var fileName = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(url)).TrimEnd('=') + ".gif";
                        var localPath = Path.Combine(cacheDir, fileName);
                        if (!File.Exists(localPath) || new FileInfo(localPath).Length == 0)
                        {
                            using var resp = await s_http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                            resp.EnsureSuccessStatusCode();
                            await using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                            await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                        }
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                ApplyAnimatedImageAttachedProperties(gifControl, new Uri(localPath));
                            }
                            catch { try { RemoteImage.SetSourceUri(gifControl, url); } catch { } }
                        });
                    }
                    catch { Dispatcher.UIThread.Post(() => { try { RemoteImage.SetSourceUri(gifControl, url); } catch { } }); }
                });
                return;
            }
            // Attached property owner type name: ImageBehavior in AnimatedImage.Avalonia
            ApplyAnimatedImageAttachedProperties(gifControl, uri);
        }
        catch
        {
            try { RemoteImage.SetSourceUri(gifControl, url); } catch { }
        }
    }

    private static void ApplyAnimatedImageAttachedProperties(Image gifControl, Uri source)
    {
        // Strongly-typed assignment via AnimatedImage.Avalonia for the source itself
        gifControl.SetValue(ImageBehavior.AnimatedSourceProperty, source);
        // Use reflection for optional flags to keep compatibility across versions
        try
        {
            if (typeof(ImageBehavior).GetField("AutoStartProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is AvaloniaProperty apAuto)
                gifControl.SetValue(apAuto, true);
            else if (typeof(ImageBehavior).GetField("IsAnimationActiveProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is AvaloniaProperty apActive)
                gifControl.SetValue(apActive, true);
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e) { base.OnClosed(e); }

    private void SafeLog(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_logPath, $"[{DateTime.Now:O}] {message}\n");
        }
        catch { }
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
