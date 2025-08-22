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
    private string _sizeMode = "Fit to window"; // or Original
    private bool _isSeeking;
    private string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs", "viewer.log");

    public ViewerWindow()
    {
        try
        {
            // Create log before any XAML is loaded, so crashes in XAML still produce a log
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
            this.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
            // Record that the viewer was constructed successfully
            SafeLog("ViewerWindow constructed");
            this.Opened += async (_, __) =>
            {
                SafeLog("ViewerWindow opened");
                try { await LoadAsync(); }
                catch (Exception ex)
                {
                    SafeLog("LoadAsync crash: " + ex.ToString());
                    ShowStartupError(ex);
                }
            };
            this.SizeChanged += (_, __) => ApplySizeModeFitIfNeeded();
        }
        catch (Exception ex)
        {
            SafeLog("InitializeComponent failed: " + ex.ToString());
            ShowStartupError(ex);
        }
    }

    private void ShowStartupError(Exception ex)
    {
        try
        {
            this.Content = new TextBlock
            {
                Text = $"Viewer failed to start:\n{ex.Message}\n{ex}",
                Foreground = Brushes.Red,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap
            };
        }
        catch { }
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

		// Hide all containers initially
		imageBorder.IsVisible = false;
		videoBorder.IsVisible = false;
		img.IsVisible = false;
		gif.IsVisible = false;

		if (isVideo)
		{
			videoBorder.IsVisible = true;
			var settings = App.Services?.GetService<Furchive.Core.Interfaces.ISettingsService>();
			bool webViewEnabled = settings?.GetSetting<bool>("WebViewEnabled", true) ?? true;

			if (!webViewEnabled)
			{
				videoHost.Content = new TextBlock { Text = "Video playback requires WebView; please enable it in settings.", Foreground = Brushes.White, Margin = new Thickness(12) };
				SafeLog("WebView is disabled in settings.");
				return;
			}

#if HAS_WEBVIEW_AVALONIA
			try
			{
				var proxy = App.Services?.GetService<ILocalMediaProxy>();
				var pageUrl = proxy?.BaseAddress != null && !string.IsNullOrWhiteSpace(bestUrl) ? proxy.GetPlayerUrl(bestUrl!) : bestUrl;
				SafeLog($"Attempting video playback: pageUrl={pageUrl}");

				if (string.IsNullOrWhiteSpace(pageUrl))
				{
					videoHost.Content = new TextBlock { Text = "Video source URL is missing.", Foreground = Brushes.White, Margin = new Thickness(12) };
					SafeLog("Video playback failed: No URL.");
					return;
				}

				var webView = CreateWebViewControl();
				if (webView != null)
				{
					videoHost.Content = webView;
					TrySetWebViewAddress(webView, pageUrl!);
					SafeLog("WebView control created and address set.");
				}
				else
				{
					videoHost.Content = new TextBlock { Text = "WebView control failed to load. Please ensure WebView2 runtime is installed.", Foreground = Brushes.White, Margin = new Thickness(12) };
					SafeLog("WebView control type not found at runtime.");
				}
			}
			catch (Exception ex)
			{
				videoHost.Content = new TextBlock { Text = $"An error occurred while loading the video: {ex.Message}", Foreground = Brushes.White, Margin = new Thickness(12) };
				SafeLog("WebView backend failed: " + ex.ToString());
			}
#else
            videoHost.Content = new TextBlock { Text = "This version of Furchive was built without video support.", Foreground = Brushes.White, Margin = new Thickness(12) };
            SafeLog("Application not compiled with HAS_WEBVIEW_AVALONIA flag.");
#endif
			return; // End of video handling path
		}
		
		imageBorder.IsVisible = true;
		if (looksGif)
		{
			// GIF playback via AnimatedImage
			img.IsVisible = false;
			gif.IsVisible = true;
			if (!string.IsNullOrWhiteSpace(bestUrl))
			{
				SafeLog($"Attempting GIF playback: url={bestUrl}");
				try 
				{ 
					SetGifSource(gif, bestUrl); 
					SafeLog("SetGifSource succeeded."); 
				} 
				catch (Exception ex) 
				{ 
					SafeLog("SetGifSource failed: " + ex.ToString()); 
				}
			}
			return; // End of GIF handling path
		}

		// It's a static image, show the correct control
		img.IsVisible = true;
		gif.IsVisible = false;

		// Try local final file first (static images)
		try
		{
			var settings = App.Services?.GetService<ISettingsService>();
			var baseDir = settings?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
			var finalPath = GenerateFinalPath(m, baseDir, settings);
			SafeLog($"Checking for downloaded image at: {finalPath}");
			if (!string.IsNullOrWhiteSpace(finalPath) && File.Exists(finalPath) && IsImageFile(finalPath))
			{
				using (var stream = File.OpenRead(finalPath))
				{
					img.Source = new Bitmap(stream);
				}
				SafeLog("Loaded image from downloaded file.");
				return; // Explicitly return to prevent fallback
			}
			else
			{
				SafeLog("Downloaded image not found or not a valid image file. Checking temp.");
			}
		}
		catch (Exception ex) { SafeLog("Error loading downloaded image: " + ex.ToString()); }

		// Try temp file next
		try
		{
			var temp = GetTempPathFor(m);
			SafeLog($"Checking for temp image at: {temp}");
			if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp) && IsImageFile(temp))
			{
				using (var stream = File.OpenRead(temp))
				{
					img.Source = new Bitmap(stream);
				}
				SafeLog("Loaded image from temp file.");
				return; // Explicitly return to prevent fallback
			}
			else
			{
				SafeLog("Temp image not found or not a valid image file. Falling back to remote.");
			}
		}
		catch (Exception ex) { SafeLog("Error loading temp image: " + ex.ToString()); }

		// Remote best-quality image
		try
		{
			if (!string.IsNullOrWhiteSpace(bestUrl))
			{
				SafeLog($"Attempting remote image load: url={bestUrl}");
				RemoteImage.SetSourceUri(img, bestUrl);
				SafeLog("RemoteImage.SetSourceUri called.");
			}
			else
			{
				SafeLog("No valid remote image URL found.");
			}
		}
		catch (Exception ex) { SafeLog("Error loading remote image: " + ex.ToString()); }

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
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var sv = this.FindControl<ScrollViewer>("ImageScroll");
            if (sv == null) return;
            _isPanning = true;
            _panStartPointer = e.GetPosition(sv);
            _panStartOffset = sv.Offset;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        catch { }
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (_isPanning)
            {
                _isPanning = false;
                if (e.Pointer.Captured == this) e.Pointer.Capture(null);
                e.Handled = true;
            }
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
            var current = e.GetPosition(sv);
            var delta = current - _panStartPointer;
            // invert delta to move content with cursor drag
            var target = _panStartOffset - new Vector(delta.X, delta.Y);
            sv.Offset = new Vector(Math.Max(0, target.X), Math.Max(0, target.Y));
            e.Handled = true;
        }
        catch { }
    }

    private void OnSizeModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            var cb = sender as ComboBox;
            var item = cb?.SelectedItem as ComboBoxItem;
            _sizeMode = item?.Content?.ToString() ?? "Fit to window";
            ApplySizeModeFitIfNeeded();
        }
        catch { }
    }

    private void ApplySizeModeFitIfNeeded()
    {
        try
        {
            if (_sizeMode != "Fit to window") return;
            var img = this.FindControl<Image>("ImageView");
            if (img?.Source is Bitmap bmp)
            {
                var host = this.FindControl<ScrollViewer>("ImageScroll");
                var zoomSlider = this.FindControl<Slider>("ZoomSlider");
                if (host == null || zoomSlider == null) return;
                var availW = Math.Max(1, host.Viewport.Width - 20);
                var availH = Math.Max(1, host.Viewport.Height - 20);
                var scaleX = availW / bmp.PixelSize.Width;
                var scaleY = availH / bmp.PixelSize.Height;
                var scale = Math.Min(scaleX, scaleY);
                scale = Math.Clamp(scale, zoomSlider.Minimum, zoomSlider.Maximum);
                if (Math.Abs(zoomSlider.Value - scale) > 0.0001)
                {
                    zoomSlider.Value = scale;
                }
            }
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
