using System;
using System.IO;
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

    public ViewerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LoadMedia();
        KeyDown += OnViewerKeyDown;
        this.PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty && _autoFitEnabled)
            {
                ApplyInitialFit(force: true);
            }
        };
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
    if (e.Key == Key.Left || e.Key == Key.A)
        {
            OnPrevClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    else if (e.Key == Key.Right || e.Key == Key.D)
        {
            OnNextClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
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
}

