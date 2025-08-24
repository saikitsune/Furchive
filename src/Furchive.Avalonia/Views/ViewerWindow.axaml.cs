using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Furchive.Avalonia.Behaviors;
using Furchive.Core.Models;
// Removed conditional compilation (HAS_LIBVLC) due to intermittent unmatched #endif build errors.
// LibVLC packages are referenced unconditionally, so we always include these usings.
using LibVLCSharp.Shared; // LibVLC core
using LibVLCSharp.Avalonia; // VideoView control
using Avalonia.Platform; // For TryGetPlatformHandle / IPlatformHandle and AssetLoader
using Microsoft.Extensions.DependencyInjection;
using Furchive.Core.Interfaces;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    // Zoom & pan state
    private double _zoom = 1.0;
    private double _translateX;
    private double _translateY;
    private bool _initialFitApplied;
    private bool _autoFitEnabled = true;
    private bool _zoomPanelVisible;
    private bool _suppressZoomSlider;
    private PixelSize _lastBitmapPixelSize;

    // Panning
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartTranslateX;
    private double _panStartTranslateY;
    private DateTime _lastClickTime;
    private const int DoubleClickThresholdMs = 350;

    // Size mode
    private enum SizeMode { Fit, Original }
    private SizeMode _currentSizeMode = SizeMode.Fit;

    // Navigation
    private IReadOnlyList<MediaItem>? _items;
    private int _index;
    private int? _poolId;

    // Simple in-memory bitmap cache for adjacent prefetch
    private readonly Dictionary<string, Bitmap> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _prefetchRadius = 2; // prev 2 / next 2
    private bool _prefetchScheduled;

    // Image source change handler (reuse so we can detach on navigation)
    private EventHandler<AvaloniaPropertyChangedEventArgs>? _imageSourceHandler;
    // Logging synchronization for viewer.log writes
    private static readonly object _viewerLogLock = new();
    private static readonly System.Net.Http.HttpClient _httpClient = new();
    // LibVLC shared state
    // (Removed redundant nested #if HAS_LIBVLC) 
    private static LibVLC? _sharedLibVlc;
    private static bool _libVlcInitAttempted;
    private MediaPlayer? _activeMediaPlayer;
    private Furchive.Avalonia.Controls.VlcNativeHost? _activeVlcHost; // track current native host
    private bool _isClosing;
    // Overlay video controls state
    // Fixed video controls bar (no overlay auto-hide)
    private bool _isSeeking;
    private int _lastKnownDurationMs;
    private DispatcherTimer? _videoUiTimer;
    private double _pendingSeekPosition01 = -1; // fraction while dragging
    private double _deferredSeekFraction = -1;  // seek to apply when duration known
    private int _seekUiSuppressTicks;           // skip auto slider update after seek
    private double _lastSeekAppliedFraction = -1; // for reassert
    private DateTime _lastTickLog = DateTime.MinValue;
    private bool _updatingSeekFromPlayer; // guard to avoid feedback loop
    private double _lastVolumeBeforeMute = 1.0;
    private bool _loopEnabled;
    private bool _fullscreen;
    private string? _currentVideoUrl;
    private bool _isVideoMode; // track if current media is video
    private IDownloadService? _downloadService; // resolved from DI
    private ISettingsService? _settingsService;
    // Shortcut overlay state
    private bool _shortcutsVisible;

    public ViewerWindow()
    {
        InitializeComponent();
    // (Removed redundant nested #if HAS_LIBVLC)
    // LibVLC one-time initialization (ignore failures so image viewing still works)
    TryInitLibVlc();
        Opened += (_, _) => LoadMedia();
        KeyDown += OnViewerKeyDown;
        // Initialize loop + volume icon defaults before any media loads
        try
        {
            var loopToggle = this.FindControl<ToggleButton>("LoopToggle");
            if (loopToggle != null)
            {
                _loopEnabled = loopToggle.IsChecked == true;
                var loopImg = this.FindControl<Image>("LoopIcon");
                if (loopImg != null)
                {
                    var key = _loopEnabled ? "Icon.LoopOn" : "Icon.LoopOff";
                    if (Application.Current!.Resources[key] is Bitmap bmp) loopImg.Source = bmp;
                }
            }
            var volImg = this.FindControl<Image>("VolumeIcon");
            if (volImg != null && Application.Current!.Resources["Icon.Volume"] is Bitmap vbmp)
                volImg.Source = vbmp;
        }
        catch { }
    // (Removed overlay pointer tracking; fixed controls bar always visible)
        this.PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty && _autoFitEnabled)
            {
                ApplyInitialFit(force: true);
            }
        };

        // Resolve services (best-effort) from global App.Services
        try
        {
            var sp = Furchive.Avalonia.App.Services;
            _downloadService = sp?.GetService<IDownloadService>();
            _settingsService = sp?.GetService<ISettingsService>();
        }
        catch { }
    }

    public void InitializeNavigationContext(IReadOnlyList<MediaItem> items, int index, int? poolId)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        _poolId = poolId;
        if (_items.Count > 0)
            DataContext = _items[_index];
        UpdateNavButtonsVisibility();
        UpdateIndexLabel();
    UpdatePoolNameLabel();
        if (IsLoaded)
            LoadMedia();
    }

    private void LoadMedia()
    {
        // Ensure any existing video playback is stopped before loading new item
        try
        {
            if (_activeMediaPlayer != null)
            {
                try { _activeMediaPlayer.Stop(); } catch { }
            }
        }
        catch { }
        if (DataContext is not MediaItem item)
        {
            UpdateNavButtonsVisibility();
            return;
        }
        try { Title = string.IsNullOrWhiteSpace(item.Title) ? "Viewer" : item.Title; } catch { }
        UpdateNavButtonsVisibility();
        UpdateIndexLabel();
    UpdatePoolNameLabel();

    var img = this.FindControl<Image>("ImageElement");
        if (img == null) return;

        // Detach previous source handler to avoid multiple subscriptions
        if (_imageSourceHandler != null)
        {
            try { img.PropertyChanged -= _imageSourceHandler; } catch { }
        }

        _initialFitApplied = false;
        var url = string.IsNullOrWhiteSpace(item.FullImageUrl) ? item.PreviewUrl : item.FullImageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        // Log each attempted load (one line per navigation / data context change)
        try
        {
            var logsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
            try { Directory.CreateDirectory(logsRoot); } catch { }
            var logFile = Path.Combine(logsRoot, "viewer.log");
            var line = $"[{DateTime.Now:O}] load item index={_index + 1} total={_items?.Count ?? 0} id={item.Id} url={url}\n";
            lock (_viewerLogLock)
            {
                try { File.AppendAllText(logFile, line); } catch { }
            }
        }
        catch { }

        // Very simple video detection (extensions)
    bool isVideo = false;
        try
        {
            var cleanUrl = url.Split('?', '#')[0];
            var ext = System.IO.Path.GetExtension(cleanUrl)?.Trim('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ext))
            {
                switch (ext)
                {
                    case "mp4":
                    case "webm":
                    case "mkv":
                    case "mov":
                        isVideo = true; break;
                }
            }
        }
        catch { }

    if (isVideo)
        {
            var videoHost = this.FindControl<Grid>("VideoHost");
            if (videoHost != null)
            {
                videoHost.IsVisible = true;
                img.IsVisible = false;
                _isVideoMode = true;
                SetImageInteractionEnabled(false);
                // Hide zoom + size UI when video mode
                TrySetVisibility("ZoomButton", false);
                TrySetVisibility("SizeLabel", false);
                TrySetVisibility("SizeModeCombo", false);
                var surfaceLayer = videoHost.FindControl<Grid>("VideoSurfaceLayer");
                surfaceLayer?.Children.Clear();
                try
                {
                    (surfaceLayer ?? videoHost).Children.Add(new TextBlock
                    {
                        Text = "Initializing video...",
                        Foreground = Brushes.LightGray,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Thickness(8)
                    });
                    var targetUrl = url;
                    _ = AttemptCreateVideoViewAsync(videoHost, targetUrl);
                }
                catch (Exception vlex)
                {
                    LogViewerDiag("video-libvlc-error-schedule " + vlex.Message);
                    surfaceLayer?.Children.Clear();
                    (surfaceLayer ?? videoHost).Children.Add(new TextBlock
                    {
                        Text = "Video playback failed to schedule (LibVLC)",
                        Foreground = Brushes.OrangeRed,
                        FontSize = 16,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                    });
                }
                return;
            }
        }

        // Not a video: ensure any previous video playback is stopped and video host hidden
        try
        {
            // LibVLC disabled: skip media player disposal
            var priorVideoHost = this.FindControl<Grid>("VideoHost");
            if (priorVideoHost != null)
            {
                priorVideoHost.IsVisible = false;
                // Only clear video surface layer so controls bar persists
                var surfaceLayer = priorVideoHost.FindControl<Grid>("VideoSurfaceLayer");
                surfaceLayer?.Children.Clear();
            }
            img.IsVisible = true;
            if (_isVideoMode)
            {
                _isVideoMode = false;
                SetImageInteractionEnabled(true);
                // Restore zoom + size UI
                TrySetVisibility("ZoomButton", true);
                TrySetVisibility("SizeLabel", true);
                TrySetVisibility("SizeModeCombo", true);
                // Dispose previous media player to ensure playback stops
                try
                {
                    if (_activeMediaPlayer != null)
                    {
                        DetachVlcPlayerEvents(_activeMediaPlayer);
                        try { _activeMediaPlayer.Stop(); } catch { }
                        _activeMediaPlayer.Dispose();
                        _activeMediaPlayer = null;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Local file?
        try
        {
            if (File.Exists(url))
            {
                using var fs = File.OpenRead(url);
                if (_bitmapCache.TryGetValue(url, out var cachedBmp))
                {
                    img.Source = cachedBmp;
                }
                else
                {
                    var bmp = new Bitmap(fs);
                    _bitmapCache[url] = bmp;
                    img.Source = bmp;
                }
                ApplyInitialFit(force: true);
                if (_currentSizeMode == SizeMode.Original)
                    ApplyOriginalSize();
                SchedulePrefetch();
                return;
            }
        }
        catch { }

        // Remote: set URI and wait for bitmap to arrive
        // Remote: check cache first (prefetched)
        if (_bitmapCache.TryGetValue(url, out var remoteCached))
        {
            img.Source = remoteCached;
            ApplyInitialFit(force: true);
            if (_currentSizeMode == SizeMode.Original)
                ApplyOriginalSize();
            SchedulePrefetch();
            return;
        }

        try { RemoteImage.SetSourceUri(img, url); } catch { }
        _imageSourceHandler = (s, e) =>
        {
            if (e.Property == Image.SourceProperty && img.Source is Bitmap b)
            {
                if (_autoFitEnabled && b.PixelSize != _lastBitmapPixelSize)
                {
                    _lastBitmapPixelSize = b.PixelSize;
                    _initialFitApplied = false;
                    ApplyInitialFit(force: true);
                }
                if (_currentSizeMode == SizeMode.Original)
                    ApplyOriginalSize();
                // Cache fetched bitmap
                if (!_bitmapCache.ContainsKey(url))
                {
                    _bitmapCache[url] = b;
                }
                SchedulePrefetch();
            }
        };
        try { img.PropertyChanged += _imageSourceHandler; } catch { }
    }

    private void UpdateNavButtonsVisibility()
    {
        var prevBtn = this.FindControl<Button>("PrevButton");
        var nextBtn = this.FindControl<Button>("NextButton");
        bool inferredPool = false;
        if (!_poolId.HasValue && DataContext is MediaItem mi && mi.TagCategories != null)
        {
            try { inferredPool = mi.TagCategories.ContainsKey("pool_name") || mi.TagCategories.ContainsKey("pool_id"); } catch { }
        }
        bool poolContext = _poolId.HasValue || inferredPool;
        bool show = poolContext && _items != null && _items.Count > 1;
        if (prevBtn != null)
        {
            prevBtn.IsVisible = show;
            prevBtn.IsEnabled = show && _index > 0;
        }
        if (nextBtn != null)
        {
            nextBtn.IsVisible = show;
            nextBtn.IsEnabled = show && _items != null && _index < _items.Count - 1;
        }
        var idxLabel = this.FindControl<TextBlock>("IndexLabel");
        if (idxLabel != null) idxLabel.IsVisible = show;
    }

    private void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_items == null || _items.Count == 0) return;
            if (_index <= 0) return;
            _index--;
            DataContext = _items[_index];
            LoadMedia();
        }
        catch { }
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_items == null || _items.Count == 0) return;
            if (_index >= _items.Count - 1) return;
            _index++;
            DataContext = _items[_index];
            LoadMedia();
        }
        catch { }
    }

    private void UpdateIndexLabel()
    {
        var idxLabel = this.FindControl<TextBlock>("IndexLabel");
        if (idxLabel == null) return;
        if (_items == null || _items.Count == 0)
        {
            idxLabel.Text = string.Empty;
            return;
        }
        idxLabel.Text = $"{_index + 1} / {_items.Count}";
    }

    private void OnViewerKeyDown(object? sender, KeyEventArgs e)
    {
        // If shortcuts overlay open, allow Esc to close it first
        if (_shortcutsVisible && e.Key == Key.Escape)
        {
            ToggleShortcutsOverlay(false);
            e.Handled = true;
            return;
        }

        // Always allow ESC to close viewer (unless overlay just handled)
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        // Video mode specific shortcuts override navigation (A/D, Left/Right) while a video is active
        if (_isVideoMode && _activeMediaPlayer != null)
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            switch (e.Key)
            {
                case Key.Space:
                case Key.K:
                    OnPlayPauseClick(this, new RoutedEventArgs());
                    e.Handled = true; return;
                case Key.Left:
                    SeekRelative(shift ? -15000 : -5000); e.Handled = true; return;
                case Key.Right:
                    SeekRelative(shift ? 15000 : 5000); e.Handled = true; return;
                case Key.Up:
                    AdjustVolume(+5); e.Handled = true; return;
                case Key.Down:
                    AdjustVolume(-5); e.Handled = true; return;
                case Key.M:
                    OnMuteClick(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.F:
                    OnFullscreenClick(this, new RoutedEventArgs()); e.Handled = true; return;
                case Key.L:
                    var loopToggle = this.FindControl<ToggleButton>("LoopToggle");
                    if (loopToggle != null) loopToggle.IsChecked = !(loopToggle.IsChecked ?? false);
                    e.Handled = true; return;
                case Key.OemPlus:
                case Key.Add:
                    StepSpeed(+1); e.Handled = true; return;
                case Key.OemMinus:
                case Key.Subtract:
                    StepSpeed(-1); e.Handled = true; return;
            }
            // Block A/D navigation while in video mode so they are free for future use if desired
            if (e.Key == Key.A || e.Key == Key.D) { e.Handled = true; return; }
        }
        else
        {
            // Image mode navigation (or video not active) retains original A/D & arrow navigation
            if (e.Key == Key.Left || e.Key == Key.A)
            {
                OnPrevClick(this, new RoutedEventArgs());
                e.Handled = true; return;
            }
            if (e.Key == Key.Right || e.Key == Key.D)
            {
                OnNextClick(this, new RoutedEventArgs());
                e.Handled = true; return;
            }
        }
    }

    private void SeekRelative(int deltaMs)
    {
        try
        {
            if (_activeMediaPlayer == null) return;
            if (_lastKnownDurationMs <= 0) return;
            var current = _activeMediaPlayer.Time;
            var target = Math.Clamp(current + deltaMs, 0, _lastKnownDurationMs > 0 ? _lastKnownDurationMs : int.MaxValue);
            _activeMediaPlayer.Time = target;
            if (_lastKnownDurationMs > 0)
            {
                var frac = Math.Clamp((double)target / _lastKnownDurationMs, 0, 1);
                _activeMediaPlayer.Position = (float)frac;
                _seekUiSuppressTicks = 2;
            }
        }
        catch { }
    }

    private void AdjustVolume(int deltaPercent)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            var newVol = Math.Clamp(_activeMediaPlayer.Volume + deltaPercent, 0, 100);
            _activeMediaPlayer.Volume = newVol;
            var volSlider = this.FindControl<Slider>("VolumeSlider"); if (volSlider != null) volSlider.Value = newVol;
        }
        catch { }
    }

    private void StepSpeed(int direction)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            // Define discrete speed steps
            float[] steps = new float[] { 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f, 2.5f };
            float current = 1.0f;
            try { current = _activeMediaPlayer.Rate; } catch { }
            int idx = 0;
            for (int i = 0; i < steps.Length; i++) { if (Math.Abs(steps[i] - current) < 0.001f) { idx = i; break; } }
            idx = Math.Clamp(idx + direction, 0, steps.Length - 1);
            try { _activeMediaPlayer.SetRate(steps[idx]); } catch { }
        }
        catch { }
    }

    private void OnShowShortcutsClick(object? sender, RoutedEventArgs e)
    {
        ToggleShortcutsOverlay(!_shortcutsVisible);
    }

    private void ToggleShortcutsOverlay(bool show)
    {
        _shortcutsVisible = show;
        try
        {
            var ov = this.FindControl<Border>("ShortcutsOverlay");
            if (ov != null) ov.IsVisible = show;
            // Ensure overlay is above native video host; hide host input if needed
            if (_activeVlcHost != null)
            {
                _activeVlcHost.IsVisible = !show; // collapse video rendering while overlay active to avoid native window drawing above overlay
            }
        }
        catch { }
    }

    private void ApplyInitialFit(bool force = false)
    {
        if (_initialFitApplied && !force) return;
        var img = this.FindControl<Image>("ImageElement");
        var host = this.FindControl<Grid>("ViewportHost");
        if (img?.Source is not Bitmap || host == null) return;
        if (host.Bounds.Width <= 0 || host.Bounds.Height <= 0)
        {
            Dispatcher.UIThread.Post(() => ApplyInitialFit(force), DispatcherPriority.Render);
            return;
        }
        _zoom = 1.0;
        _translateX = 0;
        _translateY = 0;
        UpdateZoomTransform();
        UpdateZoomUi();
        _initialFitApplied = true;
    }

    private void ApplyOriginalSize()
    {
        var img = this.FindControl<Image>("ImageElement");
        var host = this.FindControl<Grid>("ViewportHost");
        if (img?.Source is not Bitmap bmp || host == null) return;
        double logicalW = bmp.PixelSize.Width;
        double logicalH = bmp.PixelSize.Height;
        if (bmp.Dpi.X > 0 && Math.Abs(bmp.Dpi.X - 96) > 0.1) logicalW = bmp.PixelSize.Width * 96.0 / bmp.Dpi.X;
        if (bmp.Dpi.Y > 0 && Math.Abs(bmp.Dpi.Y - 96) > 0.1) logicalH = bmp.PixelSize.Height * 96.0 / bmp.Dpi.Y;
        var fitScale = Math.Min(host.Bounds.Width / logicalW, host.Bounds.Height / logicalH);
        if (fitScale <= 0 || double.IsNaN(fitScale) || double.IsInfinity(fitScale)) fitScale = 1.0;
        _zoom = 1.0 / fitScale;
        _translateX = 0;
        _translateY = 0;
        _autoFitEnabled = false;
        UpdateZoomTransform();
        UpdateZoomUi();
    }

    private void ResetToFit()
    {
        _autoFitEnabled = true;
        _currentSizeMode = SizeMode.Fit;
        var combo = this.FindControl<ComboBox>("SizeModeCombo");
        if (combo != null && combo.SelectedIndex != 0) combo.SelectedIndex = 0;
        _initialFitApplied = false;
        ApplyInitialFit(force: true);
    }

    private void UpdateZoomUi()
    {
        var slider = this.FindControl<Slider>("ZoomSlider");
        var pct = this.FindControl<TextBlock>("ZoomPercent");
        if (slider != null)
        {
            var val = Math.Clamp(_zoom * 100.0, slider.Minimum, slider.Maximum);
            if (Math.Abs(slider.Value - val) > 0.1)
            {
                _suppressZoomSlider = true;
                try { slider.Value = val; }
                finally { _suppressZoomSlider = false; }
            }
        }
        if (pct != null) pct.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void OnSizeModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var combo = sender as ComboBox; if (combo == null) return;
        var newMode = combo.SelectedIndex == 1 ? SizeMode.Original : SizeMode.Fit;
        if (newMode == _currentSizeMode) return;
        _currentSizeMode = newMode;
        if (newMode == SizeMode.Fit)
        {
            _autoFitEnabled = true;
            _initialFitApplied = false;
            ApplyInitialFit(force: true);
        }
        else
        {
            ApplyOriginalSize();
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        if (e.GetCurrentPoint(host).Properties.IsLeftButtonPressed)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastClickTime).TotalMilliseconds <= DoubleClickThresholdMs)
            {
                ResetToFit();
                _lastClickTime = DateTime.MinValue;
                return;
            }
            _lastClickTime = now;
            _isPanning = true;
            _panStartPoint = e.GetPosition(host);
            _panStartTranslateX = _translateX;
            _panStartTranslateY = _translateY;
            host.Cursor = new Cursor(StandardCursorType.SizeAll);
            e.Pointer.Capture(host);
        }
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        var current = e.GetPosition(host);
        _translateX = _panStartTranslateX + (current.X - _panStartPoint.X);
        _translateY = _panStartTranslateY + (current.Y - _panStartPoint.Y);
        UpdateZoomTransform();
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        _isPanning = false;
        host.Cursor = Cursor.Default;
        if (e.Pointer.Captured == host)
            e.Pointer.Capture(null);
    }

    private void OnZoomButtonClick(object? sender, RoutedEventArgs e)
    {
        _zoomPanelVisible = !_zoomPanelVisible;
        var panel = this.FindControl<Border>("ZoomPanel");
        if (panel != null) panel.IsVisible = _zoomPanelVisible;
    }

    private void OnZoomSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var slider = sender as Slider; if (slider == null) return;
        if (_suppressZoomSlider) return;
        _zoom = slider.Value / 100.0;
        _autoFitEnabled = false;
        UpdateZoomTransform();
        UpdateZoomUi();
        CenterAnchorAfterZoom();
    }

    private void CenterAnchorAfterZoom()
    {
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        var center = new Point(host.Bounds.Width / 2, host.Bounds.Height / 2);
        AnchorZoom(center, applyScale: false, keepPoint: true);
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        var img = this.FindControl<Image>("ImageElement"); if (img?.Source is not Bitmap) return;
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        double factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
        var cursor = e.GetPosition(host);
        AnchorZoom(cursor, applyScale: true, scaleFactor: factor);
        e.Handled = true;
    }

    private void AnchorZoom(Point viewportPoint, bool applyScale, double scaleFactor = 1.0, bool keepPoint = false)
    {
        var img = this.FindControl<Image>("ImageElement"); if (img?.Source is not Bitmap bmp) return;
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        double oldZoom = _zoom;
        if (applyScale)
        {
            _zoom = Math.Clamp(_zoom * scaleFactor, 0.10, 10.0);
            _autoFitEnabled = false;
        }
        double newZoom = _zoom;
        if (!applyScale && !keepPoint) return;

        var hostW = host.Bounds.Width; var hostH = host.Bounds.Height;
        double bmpW = bmp.PixelSize.Width; double bmpH = bmp.PixelSize.Height;
        if (bmp.Dpi.X > 0 && Math.Abs(bmp.Dpi.X - 96) > 0.1) bmpW = bmp.PixelSize.Width * 96.0 / bmp.Dpi.X;
        if (bmp.Dpi.Y > 0 && Math.Abs(bmp.Dpi.Y - 96) > 0.1) bmpH = bmp.PixelSize.Height * 96.0 / bmp.Dpi.Y;
        var fitScale = Math.Min(hostW / bmpW, hostH / bmpH);
        var displayW = bmpW * fitScale; var displayH = bmpH * fitScale;
        var baseOffsetX = (hostW - displayW) / 2.0;
        var baseOffsetY = (hostH - displayH) / 2.0;
        var worldX = (viewportPoint.X - baseOffsetX - _translateX) / (fitScale * oldZoom);
        var worldY = (viewportPoint.Y - baseOffsetY - _translateY) / (fitScale * oldZoom);
        _translateX = viewportPoint.X - baseOffsetX - worldX * (fitScale * newZoom);
        _translateY = viewportPoint.Y - baseOffsetY - worldY * (fitScale * newZoom);
        NormalizeTranslation(bmp.PixelSize.Width, bmp.PixelSize.Height, bmp.Dpi, fitScale);
        UpdateZoomTransform();
        UpdateZoomUi();
    }

    private void UpdateZoomTransform()
    {
        var img = this.FindControl<Image>("ImageElement"); if (img == null) return;
        if (_zoom <= 0 || !double.IsFinite(_zoom)) _zoom = 1.0;
        img.RenderTransform = new TransformGroup
        {
            Children = new Transforms
            {
                new ScaleTransform(_zoom, _zoom),
                new TranslateTransform(_translateX, _translateY)
            }
        };
    }

    private void SchedulePrefetch()
    {
        if (_prefetchScheduled) return;
        _prefetchScheduled = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await PrefetchAdjacentAsync(); } catch { }
            finally { _prefetchScheduled = false; }
        }, DispatcherPriority.Background);
    }

    private async Task PrefetchAdjacentAsync()
    {
        if (_items == null || _items.Count == 0) return;
        var img = this.FindControl<Image>("ImageElement"); if (img == null) return;
        // Collect target indices
        var targets = new List<int>();
        for (int delta = 1; delta <= _prefetchRadius; delta++)
        {
            var forward = _index + delta; if (forward < _items.Count) targets.Add(forward);
            var backward = _index - delta; if (backward >= 0) targets.Add(backward);
        }
        foreach (var ti in targets)
        {
            MediaItem m; try { m = _items[ti]; } catch { continue; }
            var url = string.IsNullOrWhiteSpace(m.FullImageUrl) ? m.PreviewUrl : m.FullImageUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            if (_bitmapCache.ContainsKey(url)) continue;
            // Skip if local file missing
            try
            {
                if (File.Exists(url))
                {
                    using var fs = File.OpenRead(url);
                    var bmp = new Bitmap(fs);
                    _bitmapCache[url] = bmp;
                    continue;
                }
            }
            catch { }
            // Remote fetch via RemoteImage helper (load into temp Image control)
            var temp = new Image();
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<AvaloniaPropertyChangedEventArgs>? handler = null;
            handler = (s, e) =>
            {
                if (e.Property == Image.SourceProperty && temp.Source is Bitmap b)
                {
                    _bitmapCache[url] = b;
                    try { temp.PropertyChanged -= handler; } catch { }
                    tcs.TrySetResult(true);
                }
            };
            temp.PropertyChanged += handler;
            try { RemoteImage.SetSourceUri(temp, url); } catch { tcs.TrySetResult(false); }
            // Allow up to 8 seconds for a prefetch; ignore result
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)); await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, cts.Token)); } catch { }
        }
    }

    private void UpdatePoolNameLabel()
    {
        var label = this.FindControl<TextBlock>("PoolNameLabel"); if (label == null) return;
        if (_items == null || _items.Count == 0 || _index < 0 || _index >= _items.Count)
        {
            label.IsVisible = false; label.Text = string.Empty; return;
        }
        var item = _items[_index];
        string poolName = string.Empty;
        try
        {
            if (item.TagCategories != null)
            {
                if (item.TagCategories.TryGetValue("pool_name", out var list) && list.Count > 0) poolName = list[0];
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(poolName)) { label.IsVisible = false; label.Text = string.Empty; }
        else { label.IsVisible = true; label.Text = poolName; }
    }

    private void NormalizeTranslation(double pixelWidth, double pixelHeight, Vector? dpi = null, double fitScale = 1.0)
    {
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        double logicalWidth = pixelWidth;
        double logicalHeight = pixelHeight;
        if (dpi.HasValue)
        {
            if (dpi.Value.X > 0 && Math.Abs(dpi.Value.X - 96) > 0.1) logicalWidth = pixelWidth * 96.0 / dpi.Value.X;
            if (dpi.Value.Y > 0 && Math.Abs(dpi.Value.Y - 96) > 0.1) logicalHeight = pixelHeight * 96.0 / dpi.Value.Y;
        }
        double scaledW = logicalWidth * fitScale * _zoom;
        double scaledH = logicalHeight * fitScale * _zoom;
        if (scaledW <= host.Bounds.Width) _translateX = 0;
        if (scaledH <= host.Bounds.Height) _translateY = 0;
    }

    private static void LogViewerDiag(string line)
    {
        try
        {
            var logsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
            Directory.CreateDirectory(logsRoot);
            File.AppendAllText(Path.Combine(logsRoot, "viewer.log"), $"[{DateTime.Now:O}] {line}\n");
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
    // (Removed redundant nested #if HAS_LIBVLC)
        SafeDisposePlayer();
    }

// (Removed redundant nested #if HAS_LIBVLC)
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _isClosing = true;
        SafeDisposePlayer();
        base.OnClosing(e);
    }

    private void SafeDisposePlayer()
    {
        try
        {
            var mp = _activeMediaPlayer;
            if (mp != null)
            {
                try { DetachVlcPlayerEvents(mp); } catch { }
                try { mp.Stop(); } catch { }
                // Clear HWND (best-effort) before dispose so libvlc no longer targets destroyed child window.
                try { mp.Hwnd = IntPtr.Zero; } catch { }
                try { mp.Dispose(); } catch { }
            }
        }
        catch { }
        finally
        {
            _activeMediaPlayer = null;
            _activeVlcHost = null;
        }
    }

    private bool HasPlatformHandle()
    {
        try
        {
            return GetNativeWindowHandle() != IntPtr.Zero;
        }
        catch { return false; }
    }

    private IntPtr GetNativeWindowHandle()
    {
        try
        {
            var ph = this.TryGetPlatformHandle();
            if (ph != null)
                return ph.Handle;
        }
        catch { }
        return IntPtr.Zero;
    }

    private async Task AttemptCreateVideoViewAsync(Grid host, string url)
    {
        // Retry waiting for native window handle to avoid native control host crash (unable to create child window)
        const int maxAttempts = 15; // ~750ms
        int attempt;
        var start = DateTime.UtcNow;
        for (attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (HasPlatformHandle()) break;
            await Task.Delay(50);
        }
        if (!HasPlatformHandle())
        {
            var elapsedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;
            LogViewerDiag("video-libvlc-handle-timeout attempts=" + attempt + " elapsedMs=" + elapsedMs);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var sl = host.FindControl<Grid>("VideoSurfaceLayer");
                if (sl != null) sl.Children.Clear();
                (sl ?? host).Children.Add(new TextBlock
                {
                    Text = "Video unavailable (window handle not ready)",
                    Foreground = Brushes.OrangeRed,
                    FontSize = 14,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12)
                });
            }, DispatcherPriority.Render);
            return;
        }

    // (Removed redundant nested #if HAS_LIBVLC)
    // Ensure LibVLC is initialized before creating VideoView
    if (!TryInitLibVlc())
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var sl = host.FindControl<Grid>("VideoSurfaceLayer");
                if (sl != null) sl.Children.Clear();
                (sl ?? host).Children.Add(new TextBlock
                {
                    Text = "Video playback unavailable (LibVLC init failed)",
                    Foreground = Brushes.OrangeRed,
                    FontSize = 14,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12)
                });
            }, DispatcherPriority.Render);
            return;
        }

    await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var elapsedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                LogViewerDiag($"video-libvlc-create attempt={attempt} elapsedMs={elapsedMs} url={url}");
                var surfaceLayer = host.FindControl<Grid>("VideoSurfaceLayer");
                surfaceLayer?.Children.Clear();

                // Dispose any previous player
                try
                {
                    _activeMediaPlayer?.Stop();
                    DetachVlcPlayerEvents(_activeMediaPlayer);
                    _activeMediaPlayer?.Dispose();
                }
                catch { }

                if (_sharedLibVlc == null)
                {
                    host.Children.Add(new TextBlock
                    {
                        Text = "LibVLC not available (null instance)",
                        Foreground = Brushes.OrangeRed,
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                        Margin = new Thickness(12)
                    });
                    return;
                }

                _activeMediaPlayer = new MediaPlayer(_sharedLibVlc);
                AttachVlcPlayerEvents(_activeMediaPlayer);
                // Sync initial icons now that player exists
                try
                {
                    var pp = this.FindControl<Image>("PlayPauseIcon");
                    if (pp != null && Application.Current!.Resources["Icon.Pause"] is Bitmap pbmp) pp.Source = pbmp; // start assuming playing attempt
                    var loopImg2 = this.FindControl<Image>("LoopIcon");
                    if (loopImg2 != null)
                    {
                        var key = _loopEnabled ? "Icon.LoopOn" : "Icon.LoopOff";
                        if (Application.Current!.Resources[key] is Bitmap lbmp) loopImg2.Source = lbmp;
                    }
                    var volImg2 = this.FindControl<Image>("VolumeIcon");
                    if (volImg2 != null && Application.Current!.Resources["Icon.Volume"] is Bitmap vbmp2) volImg2.Source = vbmp2;
                }
                catch { }

                // Custom native host (Windows) to embed LibVLC directly.
                Control renderSurface;
                IntPtr pendingHwnd = IntPtr.Zero;
                bool hwndAssigned = false;
                if (OperatingSystem.IsWindows())
                {
                    var vlcHost = new Furchive.Avalonia.Controls.VlcNativeHost
                    {
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch
                    };
                    vlcHost.HandleCreated += h =>
                    {
                        pendingHwnd = h;
                        TryAssignAndStart();
                    };
                    _activeVlcHost = vlcHost;
                    renderSurface = vlcHost;
                }
                else
                {
                    // Non-Windows path placeholder (LibVLC rendering host TBD)
                    renderSurface = new Border
                    {
                        Background = Brushes.Black,
                        Child = new TextBlock
                        {
                            Text = "Video playback not yet implemented on this OS",
                            Foreground = Brushes.Gray,
                            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(8)
                        }
                    };
                }

                // Insert video rendering surface at index 0 so existing overlay (declared in XAML) stays on top.
                var surfaceLayer2 = host.FindControl<Grid>("VideoSurfaceLayer");
                if (surfaceLayer2 != null)
                {
                    try { surfaceLayer2.Children.Clear(); } catch { }
                    surfaceLayer2.Children.Add(renderSurface);
                }
                else
                {
                    try { host.Children.Insert(0, renderSurface); } catch { host.Children.Add(renderSurface); }
                }
                EnsureVideoControls(host);

                void TryAssignAndStart()
                {
                    if (_isClosing) return; // don't start if window is closing
                    if (_activeMediaPlayer == null || _sharedLibVlc == null) return;
                    if (hwndAssigned) return;
                    if (OperatingSystem.IsWindows())
                    {
                        if (pendingHwnd == IntPtr.Zero) return; // wait
                        try
                        {
                            _activeMediaPlayer.Hwnd = pendingHwnd;
                            hwndAssigned = true;
                        }
                        catch (Exception setEx)
                        {
                            LogViewerDiag("video-libvlc-assign-hwnd-failed " + setEx.Message);
                            return;
                        }
                    }
                    // Start playback
                    try
                    {
                        _currentVideoUrl = url;
                        using var media = new Media(_sharedLibVlc, new Uri(url));
                        var ok = _activeMediaPlayer.Play(media);
                        LogViewerDiag(ok ? "video-libvlc-play-started" : "video-libvlc-play-did-not-start");
                        if (ok) StartVideoUiTimer();
                    }
                    catch (Exception playEx)
                    {
                        LogViewerDiag("video-libvlc-play-error " + playEx.Message);
                        var surfaceLayer = host.FindControl<Grid>("VideoSurfaceLayer");
                        surfaceLayer?.Children.Clear();
                        (surfaceLayer ?? host).Children.Add(new TextBlock
                        {
                            Text = "Video playback failed to start",
                            Foreground = Brushes.OrangeRed,
                            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                            Margin = new Thickness(12)
                        });
                    }
                }

                // Safety: timeout fallback if handle never arrives (non-Windows path or failure)
                Dispatcher.UIThread.Post(async () =>
                {
                    var start = DateTime.UtcNow;
                    while (!hwndAssigned && OperatingSystem.IsWindows() && (DateTime.UtcNow - start).TotalMilliseconds < 1500)
                    {
                        await Task.Delay(50);
                        TryAssignAndStart();
                    }
                    if (!hwndAssigned && !OperatingSystem.IsWindows())
                    {
                        // non-windows: attempt playback anyway (may rely on software callbacks later)
                        TryAssignAndStart();
                    }
                });
                try
                {
                    var bar = host.FindControl<Border>("VideoControlsBar");
                    if (bar != null)
                    {
                        bar.PointerPressed -= OnVideoBarPointerPressed;
                        bar.PointerPressed += OnVideoBarPointerPressed;
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                LogViewerDiag("video-libvlc-error-final " + ex.Message);
                var surfaceLayer = host.FindControl<Grid>("VideoSurfaceLayer");
                surfaceLayer?.Children.Clear();
                (surfaceLayer ?? host).Children.Add(new TextBlock
                {
                    Text = "Video playback failed (native host)",
                    Foreground = Brushes.OrangeRed,
                    FontSize = 16,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12)
                });
            }
    });
    }

    private void OnVideoBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var pt = e.GetPosition(this);
            if (!IsPointerOverInteractiveInBar(pt))
            {
                // Eat the event to prevent starting a drag sequence
                e.Handled = true;
            }
        }
        catch { }
    }

    // (Removed redundant nested #if HAS_LIBVLC)
    private bool TryInitLibVlc()
    {
        if (_sharedLibVlc != null) return true;
        if (_libVlcInitAttempted) return _sharedLibVlc != null;
        _libVlcInitAttempted = true;
        try
        {
            // Avoid name collision with Furchive.Core by fully qualifying
            LibVLCSharp.Shared.Core.Initialize();
            // Basic options; add more if needed (e.g., hardware accel flags per platform)
            _sharedLibVlc = new LibVLC();
            LogViewerDiag("video-libvlc-core-initialized");
            return true;
        }
        catch (Exception ex)
        {
            LogViewerDiag("video-libvlc-init-failed " + ex.Message);
            return false;
        }
    }

    private void AttachVlcPlayerEvents(MediaPlayer? mp)
    {
        if (mp == null) return;
        try
        {
            mp.Opening += OnVlcOpening;
            mp.Playing += OnVlcPlaying;
            mp.EndReached += OnVlcEndReached;
            mp.EncounteredError += OnVlcError;
        }
        catch { }
    }

    private void SetImageInteractionEnabled(bool enabled)
    {
        // Enable/disable zoom, size controls, zoom panel visibility
        try
        {
            var zoomBtn = this.FindControl<Button>("ZoomButton"); if (zoomBtn != null) zoomBtn.IsEnabled = enabled;
            var sizeCombo = this.FindControl<ComboBox>("SizeModeCombo"); if (sizeCombo != null) sizeCombo.IsEnabled = enabled;
            if (!enabled)
            {
                // Hide zoom panel if visible when disabling
                var zp = this.FindControl<Border>("ZoomPanel"); if (zp != null) zp.IsVisible = false;
            }
        }
        catch { }
    }

    private void TrySetVisibility(string name, bool visible)
    {
        try
        {
            var ctrl = this.FindControl<Control>(name); if (ctrl != null) ctrl.IsVisible = visible;
        }
        catch { }
    }

    // Intercept pointer presses on video control bar to avoid starting stray drags that break seek sync
    private bool IsPointerOverInteractiveInBar(Point p)
    {
        try
        {
            var seek = this.FindControl<Slider>("SeekSlider");
            var vol = this.FindControl<Slider>("VolumeSlider");
            if (seek != null)
            {
                var r = seek.Bounds; var o = seek.TranslatePoint(new Point(0,0), this) ?? new Point();
                var rect = new Rect(o, r.Size);
                if (rect.Contains(p)) return true;
            }
            if (vol != null)
            {
                var r2 = vol.Bounds; var o2 = vol.TranslatePoint(new Point(0,0), this) ?? new Point();
                var rect2 = new Rect(o2, r2.Size);
                if (rect2.Contains(p)) return true;
            }
        }
        catch { }
        return false;
    }

    private void DetachVlcPlayerEvents(MediaPlayer? mp)
    {
        if (mp == null) return;
        try
        {
            mp.Opening -= OnVlcOpening;
            mp.Playing -= OnVlcPlaying;
            mp.EndReached -= OnVlcEndReached;
            mp.EncounteredError -= OnVlcError;
        }
        catch { }
    }

    private void OnVlcOpening(object? sender, EventArgs e) => LogViewerDiag("video-libvlc-event opening");
    private void OnVlcPlaying(object? sender, EventArgs e) => LogViewerDiag("video-libvlc-event playing");
    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        LogViewerDiag("video-libvlc-event end-reached");
        if (!_loopEnabled) return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_activeMediaPlayer == null || _sharedLibVlc == null || string.IsNullOrWhiteSpace(_currentVideoUrl)) return;
                using var media = new Media(_sharedLibVlc, new Uri(_currentVideoUrl));
                var ok = _activeMediaPlayer.Play(media);
                LogViewerDiag(ok ? "video-libvlc-loop-restart" : "video-libvlc-loop-restart-failed");
            }
            catch (Exception ex)
            {
                LogViewerDiag("video-libvlc-loop-error " + ex.Message);
            }
        });
    }
    private void OnVlcError(object? sender, EventArgs e) => LogViewerDiag("video-libvlc-event error");
    // End of LibVLC event handlers; remaining helper methods also require LibVLC
    private void EnsureVideoControls(Grid videoHost)
    {
        var bar = videoHost.FindControl<Border>("VideoControlsBar");
        if (bar != null) bar.IsVisible = true;
        var volume = this.FindControl<Slider>("VolumeSlider");
        if (volume != null && double.IsNaN(volume.Value)) volume.Value = 80;
        var speed = this.FindControl<ComboBox>("SpeedCombo");
        if (speed != null) speed.SelectedIndex = 1; // 1.0x
    }

    private void StartVideoUiTimer()
    {
        _videoUiTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _videoUiTimer.Tick -= OnVideoUiTick; // avoid duplicates
        _videoUiTimer.Tick += OnVideoUiTick;
        _videoUiTimer.IsEnabled = true;
    }

    private void OnVideoUiTick(object? sender, EventArgs e)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            var length = _activeMediaPlayer.Length; // ms
            var time = _activeMediaPlayer.Time; // ms
            if ((DateTime.UtcNow - _lastTickLog).TotalSeconds >= 2)
            {
                try { LogViewerDiag($"video-tick time={time} length={length} pos={_activeMediaPlayer.Position:F4}"); } catch { }
                _lastTickLog = DateTime.UtcNow;
            }
            if (length > 0) _lastKnownDurationMs = (int)length;
        if (_deferredSeekFraction >= 0 && length > 0)
            {
                try
                {
            var frac = Math.Clamp(_deferredSeekFraction, 0.0, 1.0);
            ApplySeekFraction(frac, isDeferred:true);
                }
                catch { }
                finally { _deferredSeekFraction = -1; }
            }
            // Sync volume slider & button every tick
            var volSlider = this.FindControl<Slider>("VolumeSlider");
            if (volSlider != null && Math.Abs(volSlider.Value - _activeMediaPlayer.Volume) > 0.1)
            {
                try { volSlider.Value = _activeMediaPlayer.Volume; } catch { }
            }
            // Volume button content no longer replaced with text; icon image child retained.
            var seek = this.FindControl<Slider>("SeekSlider");
            if (seek != null)
            {
                seek.IsEnabled = length > 0;
                if (_seekUiSuppressTicks > 0)
                {
                    _seekUiSuppressTicks--; // allow player to settle after seek
                }
                else if (!_isSeeking && length > 0)
                {
                    var pos = Math.Clamp((double)time / length, 0, 1);
                    var target = pos * seek.Maximum;
                    if (Math.Abs(seek.Value - target) > 2)
                    {
                        _updatingSeekFromPlayer = true;
                        try { seek.Value = target; } finally { _updatingSeekFromPlayer = false; }
                    }
                }
            }
            var cur = this.FindControl<TextBlock>("CurrentTimeLabel");
            var tot = this.FindControl<TextBlock>("TotalTimeLabel");
            if (cur != null) cur.Text = FormatTime(time);
            if (tot != null) tot.Text = FormatTime(_lastKnownDurationMs);
            var volPct = this.FindControl<TextBlock>("VolumePercentLabel");
            if (volPct != null && _activeMediaPlayer != null)
            {
                try { volPct.Text = _activeMediaPlayer.Volume + "%"; } catch { }
            }
        }
        catch { }
    }

    private static string FormatTime(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        if (ts.TotalHours >= 1) return ts.ToString(@"hh\:mm\:ss");
        return ts.ToString(@"mm\:ss");
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            if (_activeMediaPlayer.IsPlaying) _activeMediaPlayer.Pause(); else _activeMediaPlayer.Play();
            var iconImg = this.FindControl<Image>("PlayPauseIcon");
            if (iconImg != null)
            {
                try
                {
                    var key = _activeMediaPlayer.IsPlaying ? "Icon.Pause" : "Icon.Play";
                    if (Application.Current!.Resources[key] is Bitmap bmp) iconImg.Source = bmp;
                }
                catch { }
            }
            var volSlider = this.FindControl<Slider>("VolumeSlider");
            if (volSlider != null && volSlider.Value != _activeMediaPlayer.Volume)
            {
                try { volSlider.Value = _activeMediaPlayer.Volume; } catch { }
            }
        }
        catch { }
    }

    private void OnSeekSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isSeeking = true;
    LogViewerDiag("video-seek-drag-begin");
    }

    private void OnSeekSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isSeeking = false;
    LogViewerDiag("video-seek-drag-end");
        CommitSeek();
    }

    private void OnSeekSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_activeMediaPlayer == null) return;
        var slider = sender as Slider; if (slider == null) return;
        // Fallback direct seek path (if pointer events suppressed by native hwnd)
        if (!_isSeeking && !_updatingSeekFromPlayer && _lastKnownDurationMs > 0 && _activeMediaPlayer != null)
        {
            var desiredFrac = slider.Value / slider.Maximum;
            var currentFrac = _lastKnownDurationMs > 0 ? Math.Clamp((double)_activeMediaPlayer.Time / _lastKnownDurationMs, 0, 1) : _activeMediaPlayer.Position;
            if (Math.Abs(desiredFrac - currentFrac) > 0.02) // >2% change
            {
                try
                {
                    var ms = (long)(_lastKnownDurationMs * desiredFrac);
                    _activeMediaPlayer.Time = ms;
                    _seekUiSuppressTicks = 2;
                    LogViewerDiag($"video-seek-fallback-applied frac={desiredFrac:F4} ms={ms}");
                }
                catch (Exception ex) { LogViewerDiag("video-seek-fallback-error " + ex.Message); }
            }
        }
        if (_isSeeking)
        {
            _pendingSeekPosition01 = slider.Value / slider.Maximum;
            LogViewerDiag($"video-seek-preview frac={_pendingSeekPosition01:F4}");
            var cur = this.FindControl<TextBlock>("CurrentTimeLabel");
            if (cur != null && _lastKnownDurationMs > 0)
            {
                var previewMs = (long)(_lastKnownDurationMs * _pendingSeekPosition01);
                cur.Text = FormatTime(previewMs);
            }
        }
    }

    private void CommitSeek()
    {
        if (_activeMediaPlayer == null) return;
        if (_pendingSeekPosition01 >= 0)
        {
            var frac = Math.Clamp(_pendingSeekPosition01, 0.0, 1.0);
            if (_lastKnownDurationMs > 0)
            {
                ApplySeekFraction(frac, isDeferred:false);
            }
            else
            {
                _deferredSeekFraction = frac;
                LogViewerDiag($"video-seek-defer frac={frac:F4}");
            }
        }
        _pendingSeekPosition01 = -1;
    }

    private void ApplySeekFraction(double frac, bool isDeferred)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            var before = _activeMediaPlayer.Position;
            _activeMediaPlayer.Position = (float)frac; // property set first
            if (_lastKnownDurationMs > 0)
            {
                var ms = (long)(_lastKnownDurationMs * frac);
                try { _activeMediaPlayer.Time = ms; } catch { }
            }
            _seekUiSuppressTicks = 3;
            _lastSeekAppliedFraction = frac;
            LogViewerDiag($"video-seek-apply {(isDeferred?"deferred":"commit")} frac={frac:F4} before={before:F4} after={_activeMediaPlayer.Position:F4}");
            // Reassert shortly after in case VLC drifts
            DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    if (_activeMediaPlayer == null) return;
                    var current = _activeMediaPlayer.Position;
                    if (Math.Abs(current - frac) > 0.03)
                    {
                        var before2 = current;
                        var wasPlaying = _activeMediaPlayer.IsPlaying;
                        if (wasPlaying) { try { _activeMediaPlayer.Pause(); } catch { } }
                        try { _activeMediaPlayer.Position = (float)frac; } catch { }
                        if (_lastKnownDurationMs > 0)
                        {
                            var ms2 = (long)(_lastKnownDurationMs * frac);
                            try { _activeMediaPlayer.Time = ms2; } catch { }
                        }
                        if (wasPlaying) { try { _activeMediaPlayer.Play(); } catch { } }
                        LogViewerDiag($"video-seek-reassert frac={frac:F4} before={before2:F4} after={_activeMediaPlayer.Position:F4}");
                        _seekUiSuppressTicks = 2;
                    }
                }
                catch { }
            }, TimeSpan.FromMilliseconds(300));
        }
        catch (Exception ex)
        {
            LogViewerDiag("video-seek-error " + ex.Message);
        }
    }

    private void OnVolumeSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_activeMediaPlayer == null) return;
        var slider = sender as Slider; if (slider == null) return;
        try
        {
            var volInt = (int)Math.Clamp(slider.Value, 0, 100);
            _activeMediaPlayer.Volume = volInt;
            if (volInt > 0) _lastVolumeBeforeMute = slider.Value / 100.0;
            var vIconImg = this.FindControl<Image>("VolumeIcon"); if (vIconImg != null)
            {
                try
                {
                    var key = volInt == 0 ? "Icon.VolumeMute" : "Icon.Volume";
                    if (Application.Current!.Resources[key] is Bitmap bmp) vIconImg.Source = bmp;
                }
                catch { }
            }
            var volPct = this.FindControl<TextBlock>("VolumePercentLabel"); if (volPct != null) { try { volPct.Text = volInt + "%"; } catch { } }
        }
        catch { }
    }

    private void OnMuteClick(object? sender, RoutedEventArgs e)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            if (_activeMediaPlayer.Volume > 0)
            {
                _lastVolumeBeforeMute = _activeMediaPlayer.Volume / 100.0;
                _activeMediaPlayer.Volume = 0;
                var volSlider = this.FindControl<Slider>("VolumeSlider"); if (volSlider != null && Math.Abs(volSlider.Value - 0) > 0.1) volSlider.Value = 0;
            }
            else
            {
                var restore = _lastVolumeBeforeMute <= 0 ? 0.8 : _lastVolumeBeforeMute;
                _activeMediaPlayer.Volume = (int)(restore * 100);
                var volSlider = this.FindControl<Slider>("VolumeSlider"); if (volSlider != null && Math.Abs(volSlider.Value - _activeMediaPlayer.Volume) > 0.1) volSlider.Value = _activeMediaPlayer.Volume;
            }
            var vIcon2 = this.FindControl<Image>("VolumeIcon"); if (vIcon2 != null)
            {
                try
                {
                    var key = _activeMediaPlayer.Volume == 0 ? "Icon.VolumeMute" : "Icon.Volume";
                    if (Application.Current!.Resources[key] is Bitmap bmp) vIcon2.Source = bmp;
                }
                catch { }
            }
        }
        catch { }
    }

    private void OnLoopToggleChanged(object? sender, RoutedEventArgs e)
    {
        _loopEnabled = (sender as ToggleButton)?.IsChecked == true;
    try
    {
        var loopImg = this.FindControl<Image>("LoopIcon");
        if (loopImg != null)
        {
            var resKey = _loopEnabled ? "Icon.LoopOn" : "Icon.LoopOff";
            try { if (Application.Current!.Resources[resKey] is Bitmap bmp) loopImg.Source = bmp; } catch { }
        }
    }
    catch { }
    }

    private async void OnDownloadButtonClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MediaItem item) return;
            if (_downloadService == null) return;
            var baseDir = _settingsService?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"))
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
            Directory.CreateDirectory(baseDir);
            await _downloadService.QueueDownloadAsync(item, baseDir);
            LogViewerDiag($"viewer-download-queued id={item.Id} title={item.Title}");
        }
        catch (Exception ex)
        {
            LogViewerDiag("viewer-download-error " + ex.Message);
        }
    }


    private void OnSpeedButtonClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var popup = this.FindControl<Popup>("SpeedPopup");
            if (popup != null)
            {
                popup.IsOpen = !popup.IsOpen;
            }
        }
        catch { }
    }

    private void OnSpeedOptionClick(object? sender, RoutedEventArgs e)
    {
        if (_activeMediaPlayer == null) return;
        try
        {
            if (sender is Button b && b.Content is string s)
            {
                var text = s;
                if (text.EndsWith("x")) text = text[..^1];
                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var spd))
                {
                    try { _activeMediaPlayer.SetRate((float)spd); } catch { }
                }
            }
        }
        catch { }
        finally
        {
            try { var popup = this.FindControl<Popup>("SpeedPopup"); if (popup != null) popup.IsOpen = false; } catch { }
        }
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
    {
        try { _fullscreen = !_fullscreen; this.WindowState = _fullscreen ? WindowState.FullScreen : WindowState.Normal; }
        catch { }
    }
// LibVLC singleton removed temporarily while resolving package restore instability
}

