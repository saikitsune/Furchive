// (duplicate usings removed)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Furchive.Avalonia.Behaviors;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Threading;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    private IReadOnlyList<MediaItem> _items = Array.Empty<MediaItem>();
    private int _index;
    private bool _dragging;
    private Point _dragStart;
    private Vector _scrollStart;
    private Stretch _stretch = Stretch.Uniform;
    private double _scale = 1.0;
    private double _fitScale = 1.0;
    private CancellationTokenSource? _loadCts;
    private string? _currentLocalPath;
    private bool _isFullscreen;
    private WindowState _prevState;
    private PixelPoint _prevPos;
    private double _prevWidth;
    private double _prevHeight;
    private static readonly HttpClient s_http = new();

    public ViewerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            // Fallback: support legacy OpenViewerMessage path which only sets DataContext
            if (_items.Count == 0 && DataContext is MediaItem single)
            {
                _items = new[] { single };
                _index = 0;
                LoadCurrentAsync();
            }
            ApplyStretchAndZoom();
        };
        SizeChanged += (_, _) => ApplyStretchAndZoom();
    }

    public void Initialize(IReadOnlyList<MediaItem> items, int index)
    {
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        LoadCurrentAsync();
        UpdateNavUi();
    }

    private MediaItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    private void UpdateNavUi()
    {
        try
        {
            var txt = this.FindControl<TextBlock>("PageNumberText");
            if (txt != null) txt.Text = BuildPageLabel(Current);
            var prevBtn = this.FindControl<Button>("PrevButton");
            var nextBtn = this.FindControl<Button>("NextButton");
            if (prevBtn != null) prevBtn.IsEnabled = _index > 0;
            if (nextBtn != null) nextBtn.IsEnabled = _index < _items.Count - 1;
        }
        catch { }
    }

    private static string BuildPageLabel(MediaItem? item)
    {
        if (item?.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var list) && list.Count > 0)
            return $"Page: {list[0]}";
        return string.Empty;
    }

    private async void LoadCurrentAsync()
    {
        var item = Current;
        if (item == null) return;
        DataContext = item;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        ShowLoading("Loading...");
        try
        {
            _currentLocalPath = await ResolveLocalOrDownloadAsync(item, ct);
            if (ct.IsCancellationRequested) return;
            await Dispatcher.UIThread.InvokeAsync(() => DisplayMedia(_currentLocalPath, item));
        }
        catch
        {
            ShowLoading("Failed");
        }
        finally
        {
            if (!ct.IsCancellationRequested) HideLoading();
        }
    }

    private void DisplayMedia(string? path, MediaItem item)
    {
    var imageBox = this.FindControl<Viewbox>("ImageBox");
    var img = this.FindControl<Image>("ImageElement");
    var videoHost = this.FindControl<ContentControl>("VideoHost");
        if (imageBox != null) imageBox.IsVisible = true; // video not yet implemented in Avalonia port
        if (videoHost != null) videoHost.IsVisible = false;
        if (img != null)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { using var fs = File.OpenRead(path); img.Source = new Bitmap(fs); }
                catch { TryRemoteImage(item, img); }
            }
            else
            {
                TryRemoteImage(item, img);
            }
        }
        _scale = 1.0;
        ApplyStretchAndZoom();
    }

    private static void TryRemoteImage(MediaItem item, Image img)
    { var url = string.IsNullOrWhiteSpace(item.FullImageUrl) ? item.PreviewUrl : item.FullImageUrl; if (!string.IsNullOrWhiteSpace(url)) { try { RemoteImage.SetSourceUri(img, url); } catch { } } }

    private async Task<string?> ResolveLocalOrDownloadAsync(MediaItem item, CancellationToken ct)
    {
        var settings = App.Services?.GetService<ISettingsService>();
        var baseDir = settings?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"))
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
        var finalPath = BuildFinalPath(item, baseDir, settings);
        if (File.Exists(finalPath)) return finalPath;
        var temp = BuildTempPath(item);
        if (File.Exists(temp)) return temp;
        var url = string.IsNullOrWhiteSpace(item.FullImageUrl) ? item.PreviewUrl : item.FullImageUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using var resp = await s_http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            await using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read);
            await resp.Content.CopyToAsync(fs, ct);
            return temp;
        }
        catch { return null; }
    }

    private static string BuildTempPath(MediaItem item)
    { var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp"); Directory.CreateDirectory(tempDir); var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExt(item.FullImageUrl) ?? "bin" : item.FileExtension; return Path.Combine(tempDir, $"{item.Source}_{item.Id}.{ext}"); }
    private static string BuildFinalPath(MediaItem item, string baseDir, ISettingsService? settings)
    {
        bool hasPool = item.TagCategories != null && (item.TagCategories.ContainsKey("page_number") || item.TagCategories.ContainsKey("pool_name"));
        var template = hasPool ? (settings?.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}")
                               : (settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}");
        string San(string s) { var inv = Path.GetInvalidFileNameChars(); return new string((s ?? string.Empty).Where(c => !inv.Contains(c)).ToArray()).Replace(" ", "_"); }
        var ext = string.IsNullOrWhiteSpace(item.FileExtension) ? TryGetExt(item.FullImageUrl) ?? "bin" : item.FileExtension;
        var rel = template.Replace("{source}", item.Source)
                          .Replace("{artist}", San(item.Artist ?? string.Empty))
                          .Replace("{id}", item.Id)
                          .Replace("{safeTitle}", San(item.Title ?? string.Empty))
                          .Replace("{ext}", ext)
                          .Replace("{pool_name}", San(item.TagCategories != null && item.TagCategories.TryGetValue("pool_name", out var p) && p.Count > 0 ? p[0] : string.Empty))
                          .Replace("{page_number}", San(item.TagCategories != null && item.TagCategories.TryGetValue("page_number", out var pn) && pn.Count > 0 ? pn[0] : string.Empty));
        return Path.Combine(baseDir, rel);
    }
    private static string? TryGetExt(string? url) { try { if (string.IsNullOrWhiteSpace(url)) return null; return Path.GetExtension(new Uri(url).AbsolutePath).Trim('.').ToLowerInvariant(); } catch { return null; } }

    private void ShowLoading(string text) { try { var ov = this.FindControl<Border>("LoadingOverlay"); var t = this.FindControl<TextBlock>("LoadingText"); if (ov != null) ov.IsVisible = true; if (t != null) t.Text = text; } catch { } }
    private void HideLoading() { try { var ov = this.FindControl<Border>("LoadingOverlay"); if (ov != null) ov.IsVisible = false; } catch { } }

    private void ApplyStretchAndZoom()
    {
    var img = this.FindControl<Image>("ImageElement");
    var sv = this.FindControl<ScrollViewer>("ViewportScroll");
    var vb = this.FindControl<Viewbox>("ImageBox");
        if (vb == null || sv == null) return;
        vb.Stretch = _stretch;
        try
        {
            if (img?.Source is Bitmap bmp)
            {
                var vw = sv.Viewport.Width; var vh = sv.Viewport.Height;
                if (vw <= 0 || vh <= 0) _fitScale = 1; else
                {
                    _fitScale = _stretch switch
                    {
                        Stretch.Uniform => Math.Min(vw / bmp.PixelSize.Width, vh / bmp.PixelSize.Height),
                        Stretch.UniformToFill => Math.Max(vw / bmp.PixelSize.Width, vh / bmp.PixelSize.Height),
                        Stretch.None => 1.0,
                        Stretch.Fill => (vw / bmp.PixelSize.Width + vh / bmp.PixelSize.Height) / 2.0,
                        _ => 1.0
                    };
                    if (_fitScale <= 0) _fitScale = 1;
                }
                var slider = this.FindControl<Slider>("ZoomSlider");
                if (slider != null)
                {
                    var total = _fitScale * _scale * 100;
                    total = Math.Clamp(total, slider.Minimum, slider.Maximum);
                    slider.Value = total;
                }
                var pct = this.FindControl<TextBlock>("ZoomPercentText");
                if (pct != null) pct.Text = $"{Math.Round(_fitScale * _scale * 100)}%";
            }
        }
        catch { }
    }

    private void OnZoomSliderChanged(object? s, RangeBaseValueChangedEventArgs e) { try { var slider = (Slider)s!; var desiredTotal = slider.Value / 100.0; _scale = _fitScale > 0 ? desiredTotal / _fitScale : desiredTotal; ApplyStretchAndZoom(); } catch { } }
    private void OnFitModeChanged(object? s, SelectionChangedEventArgs e) { try { var cb = (ComboBox?)s; var idx = cb?.SelectedIndex ?? 0; _stretch = idx switch { 1 => Stretch.UniformToFill, 2 => Stretch.None, _ => Stretch.Uniform }; ApplyStretchAndZoom(); } catch { } }
    private void OnViewportWheel(object? s, PointerWheelEventArgs e) { try { var slider = this.FindControl<Slider>("ZoomSlider"); if (slider == null) return; var mul = e.Delta.Y > 0 ? 1.1 : 0.9; var total = (slider.Value / 100.0) * mul; total = Math.Clamp(total, slider.Minimum / 100.0, slider.Maximum / 100.0); slider.Value = total * 100.0; e.Handled = true; } catch { } }
    private void OnViewportPointerPressed(object? s, PointerPressedEventArgs e) { try { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; var sv = this.FindControl<ScrollViewer>("ViewportScroll"); if (sv == null) return; _dragging = true; _dragStart = e.GetPosition(sv); _scrollStart = sv.Offset; e.Pointer.Capture(sv); e.Handled = true; } catch { } }
    private void OnViewportPointerReleased(object? s, PointerReleasedEventArgs e) { try { if (!_dragging) return; _dragging = false; if (e.Pointer.Captured != null) e.Pointer.Capture(null); e.Handled = true; } catch { } }
    private void OnViewportPointerMoved(object? s, PointerEventArgs e) { try { if (!_dragging) return; var sv = this.FindControl<ScrollViewer>("ViewportScroll"); if (sv == null) return; var cur = e.GetPosition(sv); var delta = cur - _dragStart; var target = _scrollStart - new Vector(delta.X, delta.Y); sv.Offset = new Vector(Math.Max(0, target.X), Math.Max(0, target.Y)); e.Handled = true; } catch { } }
    private void OnPrev(object? s, RoutedEventArgs e) { if (_index > 0) { _index--; LoadCurrentAsync(); UpdateNavUi(); } }
    private void OnNext(object? s, RoutedEventArgs e) { if (_index < _items.Count - 1) { _index++; LoadCurrentAsync(); UpdateNavUi(); } }
    private void OnOpenSource(object? s, RoutedEventArgs e) { try { var shell = App.Services?.GetService<IPlatformShellService>(); var url = Current?.SourceUrl ?? Current?.FullImageUrl ?? Current?.PreviewUrl; if (!string.IsNullOrWhiteSpace(url)) shell?.OpenUrl(url); } catch { } }
    private void OnDownload(object? s, RoutedEventArgs e) { try { var item = Current; if (item == null) return; var settings = App.Services?.GetService<ISettingsService>(); var baseDir = settings?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); App.Services?.GetService<IDownloadService>()?.QueueDownloadAsync(item, baseDir); } catch { } }
    private void OnOpenDownloads(object? s, RoutedEventArgs e) { try { var settings = App.Services?.GetService<ISettingsService>(); var baseDir = settings?.GetSetting<string>("DefaultDownloadDirectory", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive")) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"); App.Services?.GetService<IPlatformShellService>()?.OpenFolder(baseDir); } catch { } }
    private void OnToggleFullscreen(object? s, RoutedEventArgs e) { try { if (_isFullscreen) ExitFullscreen(); else EnterFullscreen(); } catch { } }
    private void EnterFullscreen() { if (_isFullscreen) return; _prevState = WindowState; _prevPos = Position; _prevWidth = Width; _prevHeight = Height; WindowState = WindowState.FullScreen; _isFullscreen = true; }
    private void ExitFullscreen() { if (!_isFullscreen) return; WindowState = _prevState; Position = _prevPos; Width = _prevWidth; Height = _prevHeight; _isFullscreen = false; }
    private void OnClose(object? s, RoutedEventArgs e) => Close();
    private void OnKeyDown(object? s, KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape && _isFullscreen) { ExitFullscreen(); e.Handled = true; return; }
            if (e.Key == Key.Left || e.Key == Key.A) { OnPrev(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == Key.Right || e.Key == Key.D) { OnNext(this, new RoutedEventArgs()); e.Handled = true; }
            if (e.Key == Key.F11) { OnToggleFullscreen(this, new RoutedEventArgs()); e.Handled = true; }
        }
        catch { }
    }
}
