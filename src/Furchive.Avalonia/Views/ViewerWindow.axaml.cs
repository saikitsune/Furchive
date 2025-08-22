using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Furchive.Core.Models;
using Furchive.Avalonia.Behaviors;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Furchive.Avalonia.Views;

// Minimal viewer: on open, display the media item's image (FullImageUrl preferred, else PreviewUrl).
public partial class ViewerWindow : Window
{
    private double _zoom = 1.0; // scale factor (1 = 100%)
    private bool _zoomPanelVisible;
    private bool _initialFitApplied;
    private double _translateX; // manual pan (since ScrollViewer removed)
    private double _translateY;
    private bool _autoFitEnabled = true; // while true, resizing refits image
    private PixelSize _lastBitmapPixelSize;
    private bool _suppressZoomSlider; // prevents programmatic slider updates from firing change logic
    private bool _isPanning;
    private Point _panStartPoint; // in viewport coordinates
    private double _panStartTranslateX;
    private double _panStartTranslateY;
    private DateTime _lastClickTime;
    private const int DoubleClickThresholdMs = 350;
    private enum SizeMode { Fit, Original }
    private SizeMode _currentSizeMode = SizeMode.Fit; // default

    public ViewerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LoadMedia();
        this.PropertyChanged += (_, args) =>
        {
            if (args.Property == BoundsProperty && _autoFitEnabled)
            {
                ApplyInitialFit(force: true);
            }
        };
    }

    private void LoadMedia()
    {
        if (DataContext is not MediaItem item) return;
        try { Title = string.IsNullOrWhiteSpace(item.Title) ? "Viewer" : item.Title; } catch { }
        var img = this.FindControl<Image>("ImageElement");
        if (img == null) return;
        var url = string.IsNullOrWhiteSpace(item.FullImageUrl) ? item.PreviewUrl : item.FullImageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (File.Exists(url))
            {
                using var fs = File.OpenRead(url);
                img.Source = new Bitmap(fs);
                ApplyInitialFit();
                return;
            }
        }
        catch { }
        try { RemoteImage.SetSourceUri(img, url); } catch { }
        // If remote, wait for source to appear then apply fit once.
        img.PropertyChanged += (s, e) =>
        {
            if (e.Property == Image.SourceProperty && img.Source is Bitmap b)
            {
                // If bitmap size changed (e.g., preview -> full), force a fit unless user already adjusted zoom.
                if (_autoFitEnabled && b.PixelSize != _lastBitmapPixelSize)
                {
                    _lastBitmapPixelSize = b.PixelSize;
                    ApplyInitialFit(force: true);
                }
            }
        };
    }

    private void ApplyInitialFit(bool force = false)
    {
        if (_initialFitApplied && !force) return;
        var img = this.FindControl<Image>("ImageElement");
        var host = this.FindControl<Grid>("ViewportHost");
        if (img?.Source is not Bitmap bmp || host == null)
            return;

        if (host.Bounds.Width <= 0 || host.Bounds.Height <= 0)
        {
            Dispatcher.UIThread.Post(() => ApplyInitialFit(force), DispatcherPriority.Render);
            return;
        }

        // With Stretch=Uniform the framework already sizes the image to fit while preserving aspect.
        // Treat that fitted size as zoom=1 baseline. Translation starts at 0 because alignment is Center.
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
        // Remove "fit" baseline: make baseline zoom=1 mean 1 image pixel = 1 logical unit (approx 96dpi scaling)
        double logicalW = bmp.PixelSize.Width;
        double logicalH = bmp.PixelSize.Height;
        if (bmp.Dpi.X > 0 && Math.Abs(bmp.Dpi.X - 96) > 0.1)
            logicalW = bmp.PixelSize.Width * 96.0 / bmp.Dpi.X;
        if (bmp.Dpi.Y > 0 && Math.Abs(bmp.Dpi.Y - 96) > 0.1)
            logicalH = bmp.PixelSize.Height * 96.0 / bmp.Dpi.Y;

        // Determine scale currently applied by Uniform fit so we can cancel it out by setting an appropriate zoom.
        var fitScale = Math.Min(host.Bounds.Width / logicalW, host.Bounds.Height / logicalH);
        if (fitScale <= 0 || double.IsNaN(fitScale) || double.IsInfinity(fitScale)) fitScale = 1.0;

        // We want final displayed size = logical size. Currently baseline includes fitScale at zoom=1. So required zoom = 1 / fitScale
        _zoom = 1.0 / fitScale;
        _translateX = 0;
        _translateY = 0;
        UpdateZoomTransform();
        UpdateZoomUi();
        _autoFitEnabled = false; // Original mode breaks auto fit-on-resize
    }

    private void UpdateZoomTransform()
    {
        var img = this.FindControl<Image>("ImageElement");
        if (img == null) return;
        if (_zoom <= 0 || !double.IsFinite(_zoom)) _zoom = 1.0;
        // Compose scale then translation.
        img.RenderTransform = new TransformGroup
        {
            Children = new Transforms()
            {
                new ScaleTransform(_zoom, _zoom),
                new TranslateTransform(_translateX, _translateY)
            }
        };
    }

    private void ResetToFit()
    {
        _autoFitEnabled = true;
    _currentSizeMode = SizeMode.Fit;
    var combo = this.FindControl<ComboBox>("SizeModeCombo");
    if (combo != null && combo.SelectedIndex != 0) combo.SelectedIndex = 0; // ensure UI reflects state
        _initialFitApplied = false; // force recompute
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
        else // Original
        {
            ApplyOriginalSize();
        }
    }

    // Pointer events for panning & double-click reset
    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        if (e.GetCurrentPoint(host).Properties.IsLeftButtonPressed)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastClickTime).TotalMilliseconds <= DoubleClickThresholdMs)
            {
                // Double-click: reset fit
                ResetToFit();
                _lastClickTime = DateTime.MinValue; // reset
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
        var dx = current.X - _panStartPoint.X;
        var dy = current.Y - _panStartPoint.Y;
        _translateX = _panStartTranslateX + dx;
        _translateY = _panStartTranslateY + dy;
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
    if (_suppressZoomSlider) return; // ignore programmatic changes
        _zoom = slider.Value / 100.0; // slider in percent
    _autoFitEnabled = false; // user intentionally changed zoom
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
        e.Handled = true; // suppress default
    }

    private void AnchorZoom(Point viewportPoint, bool applyScale, double scaleFactor = 1.0, bool keepPoint = false)
    {
        var img = this.FindControl<Image>("ImageElement"); if (img?.Source is not Bitmap bmp) return;
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        double oldZoom = _zoom;
        if (applyScale)
        {
            _zoom = Math.Clamp(_zoom * scaleFactor, 0.10, 10.0);
            _autoFitEnabled = false; // user interaction
        }
        double newZoom = _zoom;
        if (!applyScale && !keepPoint) return;
    // world (image) coordinates of the point under cursor before zoom
    // Current transform: screen = world*oldZoom + T
    // Reverse: world = (screen - T)/oldZoom
        // Compute displayed (fitted) size at baseline zoom=1 (Uniform). We derive from host & bitmap aspect.
        var hostW = host.Bounds.Width; var hostH = host.Bounds.Height;
        double bmpW = bmp.PixelSize.Width; double bmpH = bmp.PixelSize.Height;
        if (bmp.Dpi.X > 0 && Math.Abs(bmp.Dpi.X - 96) > 0.1) bmpW = bmp.PixelSize.Width * 96.0 / bmp.Dpi.X;
        if (bmp.Dpi.Y > 0 && Math.Abs(bmp.Dpi.Y - 96) > 0.1) bmpH = bmp.PixelSize.Height * 96.0 / bmp.Dpi.Y;
        var fitScale = Math.Min(hostW / bmpW, hostH / bmpH);
        var displayW = bmpW * fitScale; var displayH = bmpH * fitScale;
        // Center offsets (because Horizontal/VerticalAlignment=Center)
        var baseOffsetX = (hostW - displayW) / 2.0;
        var baseOffsetY = (hostH - displayH) / 2.0;
        // Current transform: screen = ((world * fitScale) * (zoom)) + baseOffset + T
        // Combine baseline fitScale into world->screen scaling factor = fitScale * zoom.
        // Existing _translateX/_translateY only represent user panning offsets T (start at 0).
        // To anchor a point, express world coords in original bitmap logical space.
        // First remove baseOffset: local = screen - baseOffset - T; world = local / (fitScale * oldZoom)
        var worldX = (viewportPoint.X - baseOffsetX - _translateX) / (fitScale * oldZoom);
        var worldY = (viewportPoint.Y - baseOffsetY - _translateY) / (fitScale * oldZoom);
        // New translation so that world point maps back to same screen point
        _translateX = viewportPoint.X - baseOffsetX - worldX * (fitScale * newZoom);
        _translateY = viewportPoint.Y - baseOffsetY - worldY * (fitScale * newZoom);
        NormalizeTranslation(bmp.PixelSize.Width, bmp.PixelSize.Height, bmp.Dpi, fitScale);
        UpdateZoomTransform();
        UpdateZoomUi();
    }

    private void NormalizeTranslation(double pixelWidth, double pixelHeight, Vector? dpi = null, double fitScale = 1.0)
    {
        var host = this.FindControl<Grid>("ViewportHost"); if (host == null) return;
        // Convert pixel dimensions to logical if DPI != 96
        double logicalWidth = pixelWidth;
        double logicalHeight = pixelHeight;
        if (dpi.HasValue)
        {
            if (dpi.Value.X > 0 && Math.Abs(dpi.Value.X - 96) > 0.1)
                logicalWidth = pixelWidth * 96.0 / dpi.Value.X;
            if (dpi.Value.Y > 0 && Math.Abs(dpi.Value.Y - 96) > 0.1)
                logicalHeight = pixelHeight * 96.0 / dpi.Value.Y;
        }
    // Displayed size at current zoom factoring baseline fitScale
    double scaledW = logicalWidth * fitScale * _zoom;
    double scaledH = logicalHeight * fitScale * _zoom;

        // Center if image smaller than viewport along an axis; otherwise leave panning as-is (future pan feature).
        if (scaledW <= host.Bounds.Width)
        {
            // center by resetting translation relative to center alignment
            _translateX = 0;
        }
        if (scaledH <= host.Bounds.Height)
        {
            _translateY = 0;
        }

        // (Optional) If we ever add panning, clamp so image covers viewport (no blank gaps) when larger.
        // For now ensure we don't inadvertently push image partly out leaving cropping when smaller.
    }
}

