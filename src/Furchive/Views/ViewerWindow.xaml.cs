using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using WpfAnimatedGif;
using System.IO;
using System.Net.Http;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

namespace Furchive.Views;

public partial class ViewerWindow : Window
{
    private readonly IUnifiedApiService _api;
    private readonly IDownloadService _downloads;
    private readonly ISettingsService _settings;
    private readonly string _source = "e621"; // e621-only
    private Func<Task<(string id, MediaItem? item)?>>? _getNext;
    private Func<Task<(string id, MediaItem? item)?>>? _getPrev;
    private Stretch _currentStretch = Stretch.Uniform; // default Contain
    private bool _isVideo => (DataContext as MediaItem)?.FileExtension?.Trim('.').ToLowerInvariant() is "mp4" or "webm" or "mov" or "m4v";
    private readonly DispatcherTimer _timer;
    // Zoom / pan state
    private double _scale = 1.0; // 1.0 == 100%
    // Fit scale: how much the image is scaled by layout/Viewbox relative to its native pixels
    private double _fitScale = 1.0;
    private System.Windows.Point _lastDragPoint;
    private bool _isDragging = false;
    private ScaleTransform _scaleTransform = new ScaleTransform(1.0, 1.0);
    private TranslateTransform _translateTransform = new TranslateTransform(0, 0);
    private CancellationTokenSource? _loadCts;
    private string? _currentLocalPath;
    private int _currentIndexInList = -1;
    private int _totalInList = 0;
    // Fullscreen state
    private bool _isFullscreen = false;
    private WindowStyle _prevWindowStyle;
    private ResizeMode _prevResizeMode;
    private WindowState _prevWindowState;
    private Rect _prevBounds;
    // Settings flags
    // (Lazy decode removed)

    public ViewerWindow(IUnifiedApiService api, IDownloadService downloads, ISettingsService settings)
    {
        // Ensure XAML components are initialized
        System.Windows.Application.LoadComponent(this, new Uri("/Furchive;component/Views/ViewerWindow.xaml", UriKind.Relative));
        _api = api;
        _downloads = downloads;
        _settings = settings;
    _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) }; // retained if needed for future UI updates
    try { this.UseLayoutRounding = true; } catch { }
        Loaded += (_, __) => ApplyStretch();
    this.Closed += ViewerWindow_Closed;
    // Defer wiring until controls are loaded
    this.Loaded += ViewerWindow_Loaded;
        this.PreviewKeyDown += ViewerWindow_PreviewKeyDown;
    }

    private void ViewerWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            // Apply GPU setting (process-wide); if disabled force software
            try
            {
                var useGpu = _settings.GetSetting<bool>("ViewerGpuAccelerationEnabled", true);
                System.Windows.Media.RenderOptions.ProcessRenderMode = useGpu
                    ? System.Windows.Interop.RenderMode.Default
                    : System.Windows.Interop.RenderMode.SoftwareOnly;
            }
            catch { }

            var img = FindName("imageViewer") as System.Windows.Controls.Image;
            if (img != null)
            {
                var tg = new TransformGroup();
                tg.Children.Add(_scaleTransform);
                tg.Children.Add(_translateTransform);
                img.RenderTransform = tg;
                img.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                // Improve scaling quality and cache for GPU pipeline
                try
                {
                    System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    img.SnapsToDevicePixels = true;
                }
                catch { }
                // Mouse wheel zoom handled on ScrollViewer; panning handled via translate transform in preview handlers
            }

            var sv = FindName("contentScrollViewer") as System.Windows.Controls.ScrollViewer;
            if (sv != null)
            {
                try { sv.UseLayoutRounding = true; } catch { }
                sv.PreviewMouseLeftButtonDown += ContentScrollViewer_PreviewMouseLeftButtonDown;
                sv.PreviewMouseLeftButtonUp += ContentScrollViewer_PreviewMouseLeftButtonUp;
                sv.PreviewMouseMove += ContentScrollViewer_PreviewMouseMove;
                sv.PreviewMouseWheel += ContentScrollViewer_PreviewMouseWheel; // already wired earlier in some flows; safe to attach
                sv.Cursor = System.Windows.Input.Cursors.Arrow;
                // When in fullscreen, a simple click exits
                sv.MouseLeftButtonDown += (s, ev) =>
                {
                    if (_isFullscreen)
                    {
                        ExitFullscreen();
                        ev.Handled = true;
                    }
                };
            }

            // Ensure container also uses high-quality scaling
            try
            {
                var imgBox = FindName("imageBox") as System.Windows.Controls.Viewbox;
                if (imgBox != null)
                {
                    System.Windows.Media.RenderOptions.SetBitmapScalingMode(imgBox, BitmapScalingMode.HighQuality);
                    imgBox.SnapsToDevicePixels = true;
                    imgBox.UseLayoutRounding = true;
                }
            }
            catch { }

            var zb = FindName("zoomButton") as System.Windows.Controls.Button;
            if (zb != null) zb.Click += (s, ev) => { try { var zp = FindName("zoomPopup") as System.Windows.Controls.Primitives.Popup; if (zp != null) zp.IsOpen = true; } catch { } };
            var zs = FindName("zoomSlider") as System.Windows.Controls.Slider;
            if (zs != null)
            {
                zs.ValueChanged += ZoomSlider_ValueChanged;
                try { zs.Minimum = 10; zs.Maximum = 500; } catch { }
                try { zs.Value = 100; } catch { }
            }
            UpdateZoomText();
        }
        catch { }
    }

    public void Initialize(MediaItem current, Func<Task<(string id, MediaItem? item)?>>? getNext, Func<Task<(string id, MediaItem? item)?>>? getPrev)
    {
        DataContext = current;
        _getNext = getNext;
        _getPrev = getPrev;
    _ = LoadIntoViewerAsync(current);
    TryUpdatePageNumberLabel(current);
    TryUpdatePoolNavigationState(current);
    }

    private void ViewerWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.A)
            {
                Prev_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Right || e.Key == System.Windows.Input.Key.D)
            {
                Next_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.F11)
            {
                if (_isFullscreen) ExitFullscreen(); else EnterFullscreen();
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape && _isFullscreen)
            {
                ExitFullscreen();
                e.Handled = true;
                return;
            }
        }
        catch { }
    }

    private void EnterFullscreen()
    {
        try
        {
            if (_isFullscreen) return;
            _prevWindowStyle = this.WindowStyle;
            _prevResizeMode = this.ResizeMode;
            _prevWindowState = this.WindowState;
            _prevBounds = new Rect(this.Left, this.Top, this.Width, this.Height);
            this.WindowStyle = WindowStyle.None;
            this.ResizeMode = ResizeMode.NoResize;
            this.Topmost = true;
            this.WindowState = WindowState.Maximized;
            this.Background = System.Windows.Media.Brushes.Black;
            _isFullscreen = true;
            ApplyStretch(); // ensure fit uses full screen
        }
        catch { }
    }

    private void ExitFullscreen()
    {
        try
        {
            if (!_isFullscreen) return;
            this.Topmost = false;
            this.WindowStyle = _prevWindowStyle;
            this.ResizeMode = _prevResizeMode;
            this.WindowState = _prevWindowState;
            // Restore bounds if not maximized
            if (_prevWindowState != WindowState.Maximized && _prevBounds.Width > 0 && _prevBounds.Height > 0)
            {
                this.Left = _prevBounds.Left;
                this.Top = _prevBounds.Top;
                this.Width = _prevBounds.Width;
                this.Height = _prevBounds.Height;
            }
            _isFullscreen = false;
            ApplyStretch();
        }
        catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_getNext == null) return;
        var res = await _getNext();
        if (res?.id != null)
        {
            var full = await _api.GetMediaDetailsAsync(_source, res.Value.id);
            var nextItem = res.Value.item ?? full; // prefer original item to preserve pool annotations
            if (nextItem != null)
            {
                DataContext = nextItem;
                TryUpdatePageNumberLabel(nextItem);
                TryUpdatePoolNavigationState(nextItem);
                await LoadIntoViewerAsync(nextItem);
            }
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_getPrev == null) return;
        var res = await _getPrev();
        if (res?.id != null)
        {
            var full = await _api.GetMediaDetailsAsync(_source, res.Value.id);
            var prevItem = res.Value.item ?? full; // prefer original item to preserve pool annotations
            if (prevItem != null)
            {
                DataContext = prevItem;
                TryUpdatePageNumberLabel(prevItem);
                TryUpdatePoolNavigationState(prevItem);
                await LoadIntoViewerAsync(prevItem);
            }
        }
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem item && !string.IsNullOrWhiteSpace(item.SourceUrl))
        {
            try { Process.Start(new ProcessStartInfo { FileName = item.SourceUrl, UseShellExecute = true }); } catch { }
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MediaItem item) return;
    var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
        var path = _settings.GetSetting<string>("DefaultDownloadDirectory", defaultDir) ?? defaultDir;
        // If we already have a temp local copy for this item, move it instead of redownloading
        try
        {
            var tempPath = GetTempPathFor(item);
            if (!string.IsNullOrWhiteSpace(_currentLocalPath) && File.Exists(_currentLocalPath) && string.Equals(_currentLocalPath, tempPath, StringComparison.OrdinalIgnoreCase))
            {
                var finalPath = BuildFinalDownloadPath(path, item);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (File.Exists(finalPath))
                {
                    // Avoid overwrite: add (1) suffix
                    var dir = Path.GetDirectoryName(finalPath)!;
                    var baseName = Path.GetFileNameWithoutExtension(finalPath);
                    var ext = Path.GetExtension(finalPath);
                    finalPath = Path.Combine(dir, baseName + " (1)" + ext);
                }
                File.Move(tempPath, finalPath);
                System.Windows.MessageBox.Show("Saved from temp.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        catch { /* fall back to queue */ }

        await _downloads.QueueDownloadAsync(item, path);
    System.Windows.MessageBox.Show("Queued download.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
    var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
        var path = _settings.GetSetting<string>("DefaultDownloadDirectory", defaultDir) ?? defaultDir;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
    }

    private void FitMode_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var idx = (sender as System.Windows.Controls.ComboBox)?.SelectedIndex ?? 0;
        _currentStretch = idx switch
        {
            1 => Stretch.UniformToFill, // Fill
            2 => Stretch.None,          // Original size
            _ => Stretch.Uniform        // Contain
        };
        ApplyStretch();
                            try { _ = Dispatcher.InvokeAsync(new Action(() => UpdateFitScaleAndSlider()), DispatcherPriority.Loaded); } catch { }
    }

    private void ApplyStretch()
    {
        try
        {
            var imgBox = FindName("imageBox") as System.Windows.Controls.Viewbox;
            if (imgBox != null) imgBox.Stretch = _currentStretch;
            // Also apply fit to WebView2 video
            ApplyWebVideoFit();
        }
        catch { /* ignore */ }
    }

    private void ZoomSlider_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        try
        {
            var v = e.NewValue; // slider value is total desired displayed percent (10..500)
            var desiredTotal = v / 100.0;
            // Compute required transform scale to achieve desired total displayed scale
            var targetTransform = _fitScale > 0 ? desiredTotal / _fitScale : desiredTotal;
            SetScale(targetTransform, preserveCenter: true);
            UpdateZoomText();
        }
        catch { }
    }

    private void UpdateZoomText()
    {
        try
        {
            var txt = FindName("zoomPercentageText") as System.Windows.Controls.TextBlock;
            if (txt != null) txt.Text = $"{Math.Round(_scale * _fitScale * 100)}%";
        }
        catch { }
    }

    private void SetScale(double newScale, bool preserveCenter)
    {
        if (newScale < 0.1) newScale = 0.1; // 10%
    if (newScale > 5.0) newScale = 5.0; // 500%
        var img = FindName("imageViewer") as System.Windows.Controls.Image;
        if (img == null) { _scale = newScale; _scaleTransform.ScaleX = _scaleTransform.ScaleY = _scale; return; }

    _scale = newScale;
    _scaleTransform.ScaleX = _scaleTransform.ScaleY = _scale;
        UpdateZoomText();
        // Change cursor to hand when zoomed in
        try
        {
            // Consider total displayed scale when deciding cursor
            img.Cursor = (_scale * _fitScale) > 1.0 ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        }
        catch { }
    }

    private void Image_MouseWheel(object? sender, System.Windows.Input.MouseWheelEventArgs e)
    {
    // Not used: wheel handled on ScrollViewer PreviewMouseWheel instead
    }

    private void Image_MouseLeftButtonDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var img = sender as System.Windows.Controls.Image;
            if (img == null) return;
            // Deprecated: image-level panning not used when ScrollViewer handlers are attached.
            // Keep method for backwards compatibility but do nothing here.
            return;
        }
        catch { }
    }

    private void Image_MouseLeftButtonUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var img = sender as System.Windows.Controls.Image;
            if (img == null) return;
            // Deprecated: do nothing
            return;
        }
        catch { }
    }

    private void Image_MouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        // Deprecated image-level move handler (ScrollViewer handlers are used now)
    }

    private void ContentScrollViewer_PreviewMouseLeftButtonDown(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var sv = sender as System.Windows.Controls.ScrollViewer;
            if (sv == null) return;
            _isDragging = true;
            _lastDragPoint = e.GetPosition(sv);
            sv.CaptureMouse();
            sv.Cursor = System.Windows.Input.Cursors.Hand;
            e.Handled = true;
        }
        catch { }
    }

    private void ContentScrollViewer_PreviewMouseLeftButtonUp(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            var sv = sender as System.Windows.Controls.ScrollViewer;
            if (sv == null) return;
            _isDragging = false;
            try { sv.ReleaseMouseCapture(); } catch { }
            sv.Cursor = System.Windows.Input.Cursors.Arrow;
            e.Handled = true;
        }
        catch { }
    }

    private void ContentScrollViewer_PreviewMouseMove(object? sender, System.Windows.Input.MouseEventArgs e)
    {
        try
        {
            if (!_isDragging) return;
            var sv = sender as System.Windows.Controls.ScrollViewer;
            if (sv == null) return;
            var pos = e.GetPosition(sv);
            var diff = pos - _lastDragPoint;
            _lastDragPoint = pos;
            try { _translateTransform.X += diff.X; } catch { }
            try { _translateTransform.Y += diff.Y; } catch { }
            e.Handled = true;
        }
        catch { }
    }

    private void ContentScrollViewer_PreviewMouseWheel(object? sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        try
        {
            // Zoom instead of scrolling when Ctrl is pressed or when mouse over image area
            // We'll always handle wheel as zoom for this viewer per user request
            var delta = e.Delta;
            var factor = delta > 0 ? 1.1 : 0.9;
            var target = Math.Clamp(_scale * factor, 0.1, 5.0);
            var sv = sender as System.Windows.Controls.ScrollViewer;
            double oldScale = _scale;
            SetScale(target, preserveCenter: false);
            // Adjust scroll offsets proportionally to keep viewport roughly at same position
            if (sv != null && oldScale > 0)
            {
                var ratio = _scale / oldScale;
                var newH = sv.HorizontalOffset * ratio;
                var newV = sv.VerticalOffset * ratio;
                try { sv.ScrollToHorizontalOffset(newH); } catch { }
                try { sv.ScrollToVerticalOffset(newV); } catch { }
            }
            // Reflect to slider
            try { var zs = FindName("zoomSlider") as System.Windows.Controls.Slider; if (zs != null) zs.Value = _scale * _fitScale * 100; } catch { }
            e.Handled = true;
        }
        catch { }
    }

    private void ViewerWindow_Closed(object? sender, EventArgs e)
    {
        try
        {
            _timer.Stop();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
            // Stop and unload WebView2/video
            try
            {
                var web = FindName("webView") as WebView2;
                if (web != null)
                {
                    try { web.CoreWebView2?.Stop(); } catch { }
                    try { web.NavigateToString("<html></html>"); } catch { }
                    try { web.Dispose(); } catch { }
                }
            }
            catch { }
        }
        catch { }
    }

    private async Task LoadIntoViewerAsync(MediaItem item)
    {
        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            // Show overlay
            var overlay = (FindName("loadingOverlay") as FrameworkElement)!;
            var overlayText = (FindName("loadingText") as System.Windows.Controls.TextBlock)!;
            var overlayProgress = (FindName("loadingProgress") as System.Windows.Controls.ProgressBar)!;
            overlay.Visibility = Visibility.Visible;
            overlayText.Text = "Loading...";
            overlayProgress.IsIndeterminate = true;
            overlayProgress.Value = 0;

            // Ensure we have a usable URL (fallback to API details if missing)
            if (string.IsNullOrWhiteSpace(item.FullImageUrl))
            {
                try
                {
                    var details = await _api.GetMediaDetailsAsync(item.Source, item.Id);
                    if (details != null && !string.IsNullOrWhiteSpace(details.FullImageUrl))
                    {
                        item.FullImageUrl = details.FullImageUrl;
                        item.PreviewUrl = string.IsNullOrWhiteSpace(item.PreviewUrl) ? (details.PreviewUrl ?? details.FullImageUrl) : item.PreviewUrl;
                        item.FileExtension = string.IsNullOrWhiteSpace(item.FileExtension) ? details.FileExtension : item.FileExtension;
                    }
                }
                catch { }
            }

            // Build best-quality-first candidates (always)
            var candidates = await BuildBestUrlCandidatesAsync(item, ct);

            // If already downloaded to final location, prefer that; else use temp path based on best candidate extension
            var localPath = GetDownloadedPathIfExists(item);
            if (string.IsNullOrEmpty(localPath))
            {
                var bestExt = TryGetExtensionFromUrl(candidates.FirstOrDefault()) ?? (string.IsNullOrWhiteSpace(item.FileExtension) ? "bin" : item.FileExtension);
                localPath = GetTempPathForWithExt(item, bestExt);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            if (File.Exists(localPath))
            {
                // Try to ensure the existing file is the best available by downloading the top candidate to a temp and replacing if it's larger/different
                var best = candidates.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(best))
                {
                    var tmp = localPath + ".new";
                    try
                    {
                        await DownloadToFileAsync(best!, tmp, p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                overlayProgress.IsIndeterminate = false;
                                overlayProgress.Value = p;
                                overlayText.Text = $"Verifying {p:0}%";
                            });
                        }, ct);
                        var oldSize = new FileInfo(localPath).Length;
                        var newSize = new FileInfo(tmp).Length;
                        // Replace if new file is significantly larger or sizes differ
                        if (newSize > oldSize + 4096 || newSize != oldSize)
                        {
                            try { File.Copy(tmp, localPath, true); } catch { }
                        }
                    }
                    catch { /* ignore verify failure */ }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                }
                _currentLocalPath = localPath;
            }
            else
            {
                // No existing file: download in priority order
                foreach (var url in candidates)
                {
                    try
                    {
                        await DownloadToFileAsync(url, localPath, p =>
                        {
                            Dispatcher.Invoke(() =>
                            {
                                overlayProgress.IsIndeterminate = false;
                                overlayProgress.Value = p;
                                overlayText.Text = $"Downloading {p:0}%";
                            });
                        }, ct);
                        _currentLocalPath = localPath;
                        break;
                    }
                    catch (Exception)
                    {
                        try { if (File.Exists(localPath)) File.Delete(localPath); } catch { }
                    }
                }
                if (string.IsNullOrWhiteSpace(_currentLocalPath))
                {
                    // Surface minimal error to user but keep UI stable
                    overlayText.Text = "Failed to load image.";
                    overlayProgress.IsIndeterminate = false;
                    overlayProgress.Value = 0;
                    return;
                }
            }

            // Decide by actual local extension
            var localExt = System.IO.Path.GetExtension(_currentLocalPath!)?.Trim('.').ToLowerInvariant();
            var isVideoLocal = localExt is "mp4" or "webm" or "mov" or "m4v";
            var isGifLocal = localExt == "gif";

            // Hide overlay
            (FindName("loadingOverlay") as FrameworkElement)!.Visibility = Visibility.Collapsed;

                        if (isVideoLocal)
            {
                var imageBoxEl = (FindName("imageBox") as FrameworkElement)!;
                var web = (FindName("webView") as WebView2)!;
                imageBoxEl.Visibility = Visibility.Collapsed;
                web.Visibility = Visibility.Visible;
                // Pass autoplay and mute via script and settings
                var autoplay = _settings.GetSetting<bool>("VideoAutoplay", true);
                var startMuted = _settings.GetSetting<bool>("VideoStartMuted", false);
                // Ensure WebView2 uses a user-writable data directory (fixes Program Files access issues)
                try
                {
                    await EnsureWebView2InitializedAsync(web);
                }
                catch { }
                // Don't use host-level mute so user can unmute via the video controls
                try { if (web.CoreWebView2 != null) web.CoreWebView2.IsMuted = false; } catch { }

                // Map the local folder to a virtual HTTPS host to bypass file:// restrictions
                try
                {
                    var folder = Path.GetDirectoryName(_currentLocalPath!)!;
                    var fileName = Path.GetFileName(_currentLocalPath!)!;
                    var host = "appassets.furchive";
                    // Re-map each time to ensure it points to the current folder
                    web.CoreWebView2?.SetVirtualHostNameToFolderMapping(host, folder, CoreWebView2HostResourceAccessKind.Allow);
                    var escaped = Uri.EscapeDataString(fileName);
                    var httpsUrl = $"https://{host}/{escaped}";
                                        var fit = GetObjectFit();

                    // Build a minimal HTML wrapper to control attributes for local files via virtual host
                    var autoplayAttr = autoplay ? "autoplay" : string.Empty;
                    var mutedAttr = startMuted ? "muted" : string.Empty;
                                                            var html = $@"<!DOCTYPE html>
<html><head><meta http-equiv='X-UA-Compatible' content='IE=edge' /><meta charset='utf-8'/>
<style>
html,body{{height:100%;margin:0;background:#000;}}
.container{{position:fixed;inset:0;background:#000;}}
video{{width:100%;height:100%;object-fit:{fit};background:#000;}}
</style>
</head><body>
<div class='container'>
                        <video id='v' {autoplayAttr} {mutedAttr} controls playsinline loop src='{httpsUrl}'></video>
                            <script>
                                (function(){{
                                    var v = document.getElementById('v');
                                    if(!v) return;
                                    // If started muted for autoplay, let the user unmute via controls
                                    v.addEventListener('volumechange', function(){{
                                        // If user unmutes, clear the muted attribute so sound plays
                                        if(!v.muted && v.volume > 0){{ v.removeAttribute('muted'); }}
                                    }});
                                }})();
                            </script>
</div>
</body></html>";
                    // Apply fit after navigation completes to ensure the video element exists
                    void handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
                    {
                        try { ApplyWebVideoFit(); } catch { }
                        try { web.NavigationCompleted -= handler; } catch { }
                    }
                    try { web.NavigationCompleted += handler; } catch { }
                    web.NavigateToString(html);
                }
                catch
                {
                    // Fallback: try direct file navigation (may fail with policy but attempt anyway)
                    try { web.Source = new Uri(_currentLocalPath!); } catch { }
                }
                // Ensure image GIF behavior is cleared when switching to video
                try { ImageBehavior.SetAnimatedSource((FindName("imageViewer") as System.Windows.Controls.Image)!, null); } catch { }
            }
            else
            {
                _timer.Stop();
                var imageBoxEl = (FindName("imageBox") as FrameworkElement)!;
                var web = (FindName("webView") as WebView2)!;
                web.Visibility = Visibility.Collapsed;
                imageBoxEl.Visibility = Visibility.Visible;

                // If GIF, start animation and loop; otherwise static image
                if (isGifLocal)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(_currentLocalPath) && File.Exists(_currentLocalPath))
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(_currentLocalPath);
                            bmp.EndInit();

                            var img = (FindName("imageViewer") as System.Windows.Controls.Image)!;
                            ImageBehavior.SetAnimatedSource(img, bmp);
                            ImageBehavior.SetRepeatBehavior(img, RepeatBehavior.Forever);
                            ImageBehavior.SetAutoStart(img, true);
                            // Reset pan on new content
                            try { _translateTransform.X = 0; _translateTransform.Y = 0; } catch { }
                                try { _ = Dispatcher.InvokeAsync(new Action(() => UpdateFitScaleAndSlider()), DispatcherPriority.Loaded); } catch { }
                        }
                    }
                    catch { }
                }
                else
                {
                    // Not a GIF: ensure animated source cleared and show static image
                    try { ImageBehavior.SetAnimatedSource((FindName("imageViewer") as System.Windows.Controls.Image)!, null); } catch { }
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(_currentLocalPath) && File.Exists(_currentLocalPath))
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(_currentLocalPath);
                            bmp.EndInit();
                            var img = (FindName("imageViewer") as System.Windows.Controls.Image)!;
                                img.Source = bmp;
                            // Reset pan on new content
                            try { _translateTransform.X = 0; _translateTransform.Y = 0; } catch { }
                            try { _ = Dispatcher.InvokeAsync(new Action(() => UpdateFitScaleAndSlider()), DispatcherPriority.Loaded); } catch { }
                        }
                    }
                    catch { }
                }
            }
            // Neighbor prefetch removed
        }
        catch { }
    }

    private async Task<List<string>> BuildBestUrlCandidatesAsync(MediaItem item, CancellationToken ct)
    {
        var list = new List<string>();
        try
        {
            // Always fetch latest details to get the true original URL
            try
            {
                var details = await _api.GetMediaDetailsAsync(item.Source, item.Id);
                if (details != null)
                {
                    if (!string.IsNullOrWhiteSpace(details.FullImageUrl)) list.Add(details.FullImageUrl!);
                    // Persist back to the item for subsequent operations
                    if (!string.IsNullOrWhiteSpace(details.FullImageUrl)) item.FullImageUrl = details.FullImageUrl;
                    if (!string.IsNullOrWhiteSpace(details.PreviewUrl) && string.IsNullOrWhiteSpace(item.PreviewUrl)) item.PreviewUrl = details.PreviewUrl;
                    if (!string.IsNullOrWhiteSpace(details.FileExtension) && string.IsNullOrWhiteSpace(item.FileExtension)) item.FileExtension = details.FileExtension;
                }
            }
            catch { }

            // Include upgraded FullImageUrl if it looks like a sample/preview by mistake
            if (!string.IsNullOrWhiteSpace(item.FullImageUrl))
            {
                var upFull = TryUpgradeE621ToOriginal(item.FullImageUrl!);
                if (!string.IsNullOrWhiteSpace(upFull)) list.Add(upFull!);
                list.Add(item.FullImageUrl!);
            }

            // If we have preview/sample, try to upgrade to original and include as fallback
            if (!string.IsNullOrWhiteSpace(item.PreviewUrl))
            {
                var upgraded = TryUpgradeE621ToOriginal(item.PreviewUrl!);
                if (!string.IsNullOrWhiteSpace(upgraded)) list.Add(upgraded!);
                list.Add(item.PreviewUrl!);
            }

            // Deduplicate and normalize
            list = list
                .Select(u => NormalizeUrl(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { }
        return list;
    }

    private static string? TryUpgradeE621ToOriginal(string url)
    {
        try
        {
            // e621 preview URLs often contain '/preview/' and 'preview_' prefix
            // sample URLs may contain '/sample/' and 'sample_' prefix
            var u = url;
            if (u.Contains("/data/preview/", StringComparison.OrdinalIgnoreCase))
            {
                u = u.Replace("/data/preview/", "/data/", StringComparison.OrdinalIgnoreCase);
                var idx = u.LastIndexOf('/');
                if (idx >= 0 && idx + 1 < u.Length)
                {
                    var path = u.Substring(0, idx + 1);
                    var file = u.Substring(idx + 1);
                    if (file.StartsWith("preview_", StringComparison.OrdinalIgnoreCase))
                        u = path + file.Substring("preview_".Length);
                }
                return u;
            }
            if (u.Contains("/data/sample/", StringComparison.OrdinalIgnoreCase))
            {
                u = u.Replace("/data/sample/", "/data/", StringComparison.OrdinalIgnoreCase);
                var idx = u.LastIndexOf('/');
                if (idx >= 0 && idx + 1 < u.Length)
                {
                    var path = u.Substring(0, idx + 1);
                    var file = u.Substring(idx + 1);
                    if (file.StartsWith("sample_", StringComparison.OrdinalIgnoreCase))
                        u = path + file.Substring("sample_".Length);
                }
                return u;
            }
        }
        catch { }
        return null;
    }

    private static async Task EnsureWebView2InitializedAsync(WebView2 web)
    {
        // If already initialized, nothing to do
        if (web.CoreWebView2 != null) return;
        // Always set a user data folder under LocalAppData to avoid write restrictions
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "Furchive", "WebView2");
        try { Directory.CreateDirectory(dataDir); } catch { }

        // Prefer passing a CoreWebView2Environment to EnsureCoreWebView2Async
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
            await web.EnsureCoreWebView2Async(env);
        }
        catch
        {
            // Fallback to CreationProperties if environment creation failed
            try
            {
                web.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = dataDir
                };
            }
            catch { }
            try { await web.EnsureCoreWebView2Async(); } catch { }
        }
    }

    private string GetTempPathFor(MediaItem item)
    {
        var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
        var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension;
        var safeArtist = string.Join("_", (item.Artist ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var safeTitle = string.Join("_", (item.Title ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var file = $"{item.Source}_{item.Id}_{safeArtist}_{safeTitle}.{ext}";
        return Path.Combine(tempDir, file);
    }

    private string BuildFinalDownloadPath(string basePath, MediaItem mediaItem)
    {
        var hasPoolContext = mediaItem.TagCategories != null && (mediaItem.TagCategories.ContainsKey("page_number") || mediaItem.TagCategories.ContainsKey("pool_name"));
        var template = hasPoolContext
            ? (_settings.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
            : (_settings.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
        var extFinal = string.IsNullOrWhiteSpace(mediaItem.FileExtension) ? TryGetExtensionFromUrl(mediaItem.FullImageUrl) ?? "bin" : mediaItem.FileExtension;
        string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray());
            return clean.Replace(" ", "_");
        }
        var filenameRel = template
            .Replace("{source}", mediaItem.Source)
            .Replace("{artist}", Sanitize(mediaItem.Artist))
            .Replace("{id}", mediaItem.Id)
            .Replace("{safeTitle}", Sanitize(mediaItem.Title))
            .Replace("{ext}", extFinal)
            .Replace("{pool_name}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : string.Empty))
            .Replace("{page_number}", Sanitize(mediaItem.TagCategories != null && mediaItem.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty));
        return Path.Combine(basePath, filenameRel);
    }

    private static string? TryGetExtensionFromUrl(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var uri = new Uri(url);
            var ext = System.IO.Path.GetExtension(uri.AbsolutePath).Trim('.').ToLowerInvariant();
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch { return null; }
    }

    private string GetTempPathForWithExt(MediaItem item, string ext)
    {
        var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
        var safeArtist = string.Join("_", (item.Artist ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var safeTitle = string.Join("_", (item.Title ?? string.Empty).Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var file = $"{item.Source}_{item.Id}_{safeArtist}_{safeTitle}.{ext}";
        return Path.Combine(tempDir, file);
    }

    private string? GetDownloadedPathIfExists(MediaItem item)
    {
        try
        {
            var defaultDir = _settings.GetSetting<string>("DefaultDownloadDirectory",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            var hasPoolContext = item.TagCategories != null && (item.TagCategories.ContainsKey("page_number") || item.TagCategories.ContainsKey("pool_name"));
            var template = hasPoolContext
                ? (_settings.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
                : (_settings.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
            string Sanitize(string s)
            {
                var invalid = Path.GetInvalidFileNameChars();
                var clean = new string((s ?? string.Empty).Where(c => !invalid.Contains(c)).ToArray());
                return clean.Replace(" ", "_");
            }
            var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExtensionFromUrl(item.FullImageUrl) ?? "bin" : item.FileExtension;
            var rel = template
                .Replace("{source}", item.Source)
                .Replace("{artist}", Sanitize(item.Artist))
                .Replace("{id}", item.Id)
                .Replace("{safeTitle}", Sanitize(item.Title))
                .Replace("{ext}", ext)
                .Replace("{pool_name}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("pool_name", out var poolNameList) && poolNameList.Count > 0 ? poolNameList[0] : string.Empty))
                .Replace("{page_number}", Sanitize(item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var pageList) && pageList.Count > 0 ? pageList[0] : string.Empty));
            var fullPath = Path.Combine(defaultDir, rel);
            if (File.Exists(fullPath)) return fullPath;
            // Fallback: search pool directories for a matching file by id
            try
            {
                var poolsRoot = Path.Combine(defaultDir, item.Source, "pools", Sanitize(item.Artist));
                if (Directory.Exists(poolsRoot))
                {
                    foreach (var file in Directory.EnumerateFiles(poolsRoot, "*", SearchOption.AllDirectories))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name != null && (name.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + item.Id, StringComparison.OrdinalIgnoreCase) || name.Contains(item.Id, StringComparison.OrdinalIgnoreCase)))
                            return file;
                    }
                }
            }
            catch { }
            return null;
        }
        catch { return null; }
    }

    private async Task DownloadToFileAsync(string url, string destinationPath, Action<double>? progress, CancellationToken ct)
    {
        using var client = new HttpClient();
        try
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
            var euserLocal = _settings.GetSetting<string>("E621Username", "") ?? "";
            var uname = string.IsNullOrWhiteSpace(euserLocal) ? "Anon" : euserLocal.Trim();
            var ua = $"Furchive/{version} (by {uname})";
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            // Some CDNs expect a Referer; provide e621 to avoid 403 on direct file access
            try { client.DefaultRequestHeaders.Referrer = new Uri("https://e621.net/"); } catch { }
        }
        catch { }
        var abs = NormalizeUrl(url);
        using var response = await client.GetAsync(abs, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1L;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var buffer = new byte[8192];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0 && progress != null)
            {
                var pct = (double)read / total * 100.0;
                progress(pct);
            }
        }
    }

    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL is empty");
        var u = url.Trim();
        if (u.StartsWith("//")) return "https:" + u;
        if (Uri.TryCreate(u, UriKind.Absolute, out _)) return u;
        if (u.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
            return "https://static1.e621.net" + u;
        if (u.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            return "https://e621.net" + u;
        return "https://e621.net/" + u.TrimStart('/');
    }

    private string GetObjectFit()
    {
        // Map WPF Stretch to CSS object-fit
        return _currentStretch switch
        {
            Stretch.Uniform => "contain",
            Stretch.UniformToFill => "cover",
            Stretch.None => "none",
            Stretch.Fill => "fill",
            _ => "contain"
        };
    }

    private void UpdateFitScaleAndSlider()
    {
        try
        {
            var img = FindName("imageViewer") as System.Windows.Controls.Image;
            var imgBox = FindName("imageBox") as System.Windows.Controls.Viewbox;
            var sv = FindName("contentScrollViewer") as System.Windows.Controls.ScrollViewer;
            if (img == null || imgBox == null || sv == null) return;

            // Determine the natural pixel size of the image
            double imgW = 0, imgH = 0;
            if (img.Source is BitmapSource bs)
            {
                imgW = bs.PixelWidth;
                imgH = bs.PixelHeight;
            }
            if (imgW <= 0 || imgH <= 0)
            {
                // Fallback: assume fit scale 1
                _fitScale = 1.0;
            }
            else
            {
                // Viewport available sizes
                var vw = sv.ViewportWidth;
                var vh = sv.ViewportHeight;
                if (double.IsNaN(vw) || vw <= 0) vw = sv.ActualWidth;
                if (double.IsNaN(vh) || vh <= 0) vh = sv.ActualHeight;
                if (vw <= 0 || vh <= 0)
                {
                    _fitScale = 1.0;
                }
                else
                {
                    // Compute scale applied by Viewbox depending on Stretch
                    switch (_currentStretch)
                    {
                        case Stretch.Uniform:
                            var scaleX = vw / imgW;
                            var scaleY = vh / imgH;
                            _fitScale = Math.Min(scaleX, scaleY);
                            break;
                        case Stretch.UniformToFill:
                            scaleX = vw / imgW;
                            scaleY = vh / imgH;
                            _fitScale = Math.Max(scaleX, scaleY);
                            break;
                        case Stretch.None:
                            _fitScale = 1.0;
                            break;
                        case Stretch.Fill:
                            _fitScale = (vw / imgW + vh / imgH) / 2.0;
                            break;
                        default:
                            _fitScale = 1.0;
                            break;
                    }
                    if (_fitScale <= 0) _fitScale = 1.0;
                }
            }

            // Ensure slider bounds
            try { var zs = FindName("zoomSlider") as System.Windows.Controls.Slider; if (zs != null) { zs.Minimum = 10; zs.Maximum = 500; zs.Value = Math.Clamp(_fitScale * _scale * 100, zs.Minimum, zs.Maximum); } } catch { }
            UpdateZoomText();
        }
        catch { }
    }

    private void ApplyWebVideoFit()
    {
        try
        {
            var web = FindName("webView") as WebView2;
            var fit = GetObjectFit();
            // Ensure video fills container and uses desired object-fit
            _ = web?.CoreWebView2?.ExecuteScriptAsync($@"(function(){{
                var v=document.querySelector('video');
                if(v){{
                    v.style.width='100%';
                    v.style.height='100%';
                    v.style.objectFit='{fit}';
                    v.style.background='#000';
                }}
            }})();");
        }
        catch { }
    }

    private void TryUpdatePageNumberLabel(MediaItem item)
    {
        try
        {
            var tb = FindName("pageNumberText") as System.Windows.Controls.TextBlock;
            if (tb == null) return;
            string page = string.Empty;
            if (item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var list) && list != null && list.Count > 0)
            {
                page = list[0];
            }
            tb.Text = string.IsNullOrWhiteSpace(page) ? string.Empty : $"Page: {page}";
        }
        catch { }
    }

    private void TryUpdatePoolNavigationState(MediaItem item)
    {
        try
        {
            var prevBtn = FindName("prevButton") as System.Windows.Controls.Button;
            var nextBtn = FindName("nextButton") as System.Windows.Controls.Button;
            if (prevBtn == null || nextBtn == null) return;

            // Infer index from page_number if present, else leave buttons enabled
            _currentIndexInList = -1;
            _totalInList = 0;
            string? page = null;
            if (item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var list) && list != null && list.Count > 0)
            {
                page = list[0];
            }
            if (!string.IsNullOrWhiteSpace(page) && int.TryParse(page, out var pageNum))
            {
                _currentIndexInList = Math.Max(1, pageNum); // 1-based
            }
            // Try to infer total from pool_name grouping: we don't have total embedded, so approximate from current SearchResults if available via Owner's DataContext
            try
            {
                if (this.Owner is MainWindow mw && mw.DataContext is ViewModels.MainViewModel vm)
                {
                    _totalInList = vm.SearchResults?.Count ?? 0;
                }
            }
            catch { }

            if (_currentIndexInList > 0 && _totalInList > 0)
            {
                prevBtn.IsEnabled = _currentIndexInList > 1;
                nextBtn.IsEnabled = _currentIndexInList < _totalInList;
                prevBtn.Opacity = prevBtn.IsEnabled ? 1.0 : 0.5;
                nextBtn.Opacity = nextBtn.IsEnabled ? 1.0 : 0.5;
            }
            else
            {
                // Unknown context; leave buttons enabled
                prevBtn.IsEnabled = true;
                nextBtn.IsEnabled = true;
                prevBtn.Opacity = 1.0;
                nextBtn.Opacity = 1.0;
            }
        }
        catch { }
    }
}
