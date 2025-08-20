using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.Loader;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Furchive.Avalonia.Behaviors;
using Furchive.Core.Interfaces;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using LibVLCSharp.Shared;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    // Deprecated WebView fields removed; using LibVLC and AnimatedImage instead
    private bool _isPanning;
    private Point _panStartPointer;
    private Vector _panStartOffset;
    private LibVLC? _libVLC;
    private MediaPlayer? _mp;
    private bool _isSeeking;
    private string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs", "viewer.log");

    public ViewerWindow()
    {
        // Create log before any XAML is loaded, so crashes in XAML still produce a log
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
        // Create VideoView lazily when needed
    var videoHost = this.FindControl<ContentControl>("VideoHost");
    var videoView = videoHost?.Content as LibVLCSharp.Avalonia.VideoView;
        if (videoHost != null && videoView == null)
        {
            try
            {
        SafeLog("Creating VideoView...");
        videoView = new LibVLCSharp.Avalonia.VideoView();
                videoHost.Content = videoView;
        SafeLog("VideoView created");
            }
            catch (Exception ex)
            {
                SafeLog("Failed to create VideoView: " + ex.Message);
            }
        }
    if (imageBorder == null || videoBorder == null || img == null) { SafeLog("Required image/video containers missing"); return; }
    if (videoBorder == null || videoView == null || gif == null) { SafeLog("Video/GIF controls missing"); return; }

        var ext = (!string.IsNullOrWhiteSpace(m.FileExtension) ? m.FileExtension : TryGetExtensionFromUrl(m.FullImageUrl) ?? TryGetExtensionFromUrl(m.PreviewUrl) ?? string.Empty).Trim('.').ToLowerInvariant();
    var isVideo = ext is "mp4" or "webm" or "mkv";
    var looksGif = ext == "gif" || LooksLikeGifFromUrl(m.FullImageUrl) || LooksLikeGifFromUrl(m.PreviewUrl);
        var bestUrl = !string.IsNullOrWhiteSpace(m.FullImageUrl) ? m.FullImageUrl : m.PreviewUrl;
    SafeLog($"Media detect: ext={ext}, isVideo={isVideo}, looksGif={looksGif}, hasUrl={!string.IsNullOrWhiteSpace(bestUrl)}");

        if (isVideo || looksGif)
        {
            if (isVideo)
            {
                imageBorder.IsVisible = false;
                gif.IsVisible = false;
                videoBorder.IsVisible = true;
                await AttachVideoAsync(bestUrl, videoView);
            }
            else
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

    private async Task AttachVideoAsync(string? url, LibVLCSharp.Avalonia.VideoView videoView)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            SafeLog($"AttachVideoAsync start: {url}");
            // Initialize LibVLC with robust probing for native binaries and plugin path
            if (!await EnsureLibVlcInitializedAsync())
            {
                SafeLog("LibVLC init failed; cannot play video.");
                return;
            }
            if (_libVLC == null) { SafeLog("LibVLC is null after init"); return; }
            _mp ??= new MediaPlayer(_libVLC);
            SafeLog("MediaPlayer created");
            videoView.MediaPlayer = _mp;

            using var media = new Media(_libVLC, new Uri(url));
            SafeLog("Media created; calling Play...");
            try { _mp.Play(media); SafeLog("Play invoked"); } catch (Exception exPlay) { SafeLog("Play threw: " + exPlay.ToString()); }

            HookVideoEvents();
            SafeLog("Video events hooked");
        }
        catch (Exception ex) { SafeLog("AttachVideoAsync exception: " + ex.Message); }
        await Task.CompletedTask;
    }

    private async Task<bool> EnsureLibVlcInitializedAsync()
    {
        if (_libVLC != null) return true;
        try
        {
            SafeLog("EnsureLibVlcInitializedAsync start");
            // Probe common libvlc locations shipped by VideoLAN.LibVLC.* packages
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "libvlc", Environment.Is64BitProcess ? "win-x64" : "win-x86"),
                Path.Combine(baseDir, "libvlc"),
                Path.Combine(baseDir, "runtimes", Environment.Is64BitProcess ? "win-x64" : "win-x86", "native"),
                baseDir
            };

            string? libDir = null;
            foreach (var c in candidates)
            {
                try
                {
                    if (Directory.Exists(c))
                    {
                        // Check for libvlc library presence
                        var hasDll = Directory.EnumerateFiles(c, "libvlc*.dll", SearchOption.TopDirectoryOnly).Any();
                        if (hasDll)
                        {
                            libDir = c;
                            SafeLog($"LibVLC dir selected: {libDir}");
                            break;
                        }
                        // Some packages place dlls one level deeper
                        var nested = Directory.Exists(Path.Combine(c, Environment.Is64BitProcess ? "win-x64" : "win-x86"))
                            ? Path.Combine(c, Environment.Is64BitProcess ? "win-x64" : "win-x86")
                            : null;
                        if (!string.IsNullOrEmpty(nested) && Directory.Exists(nested) && Directory.EnumerateFiles(nested, "libvlc*.dll", SearchOption.TopDirectoryOnly).Any())
                        {
                            libDir = nested;
                            SafeLog($"LibVLC nested dir selected: {libDir}");
                            break;
                        }
                    }
                }
                catch { }
            }

            // As a last resort, scan immediate subdirs of libvlc
            if (libDir == null)
            {
                var root = Path.Combine(baseDir, "libvlc");
                if (Directory.Exists(root))
                {
                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(root))
                        {
                            if (Directory.EnumerateFiles(sub, "libvlc*.dll", SearchOption.TopDirectoryOnly).Any())
                            { libDir = sub; break; }
                        }
                    }
                    catch { }
                }
            }

            if (!string.IsNullOrEmpty(libDir))
            {
                // Ensure native loader can find the dlls
                try
                {
                    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    if (!currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(libDir, StringComparer.OrdinalIgnoreCase))
                    {
                        Environment.SetEnvironmentVariable("PATH", libDir + ";" + currentPath);
                    }
                }
                catch { }

                try { LibVLCSharp.Shared.Core.Initialize(libDir); SafeLog("Core.Initialize(libDir) OK"); } catch { try { LibVLCSharp.Shared.Core.Initialize(); SafeLog("Core.Initialize() fallback OK"); } catch (Exception exInit) { SafeLog("Core.Initialize failed: " + exInit.Message); } }

                // Prefer setting plugin path if present
                var pluginsDir = Path.Combine(libDir, "plugins");
                if (Directory.Exists(pluginsDir))
                {
                    SafeLog($"Creating LibVLC with plugins: {pluginsDir}");
                    _libVLC = new LibVLC(new string[] { $"--plugin-path={pluginsDir}", "--no-video-title-show", "--avcodec-hw=none" });
                }
                else
                {
                    SafeLog("Creating LibVLC without explicit plugins path");
                    _libVLC = new LibVLC(new string[] { "--no-video-title-show", "--avcodec-hw=none" });
                }
                SafeLog("LibVLC created");
                return true;
            }
            else
            {
                // Try default initialization; may succeed if system-wide VLC is installed
                try { LibVLCSharp.Shared.Core.Initialize(); SafeLog("Core.Initialize(default) OK"); } catch (Exception exInit2) { SafeLog("Core.Initialize(default) failed: " + exInit2.Message); }
                _libVLC = new LibVLC(new string[] { "--no-video-title-show", "--avcodec-hw=none" });
                SafeLog("LibVLC created (default)");
                return true;
            }
        }
        catch (Exception ex)
        {
            SafeLog("EnsureLibVlcInitializedAsync failed: " + ex.ToString());
            _libVLC = null;
            return false;
        }
        finally
        {
            await Task.CompletedTask;
        }
    }

    private void HookVideoEvents()
    {
        try
        {
            var seek = this.FindControl<Slider>("SeekSlider");
            var vol = this.FindControl<Slider>("VolumeSlider");
            var cur = this.FindControl<TextBlock>("CurrentTimeText");
            var tot = this.FindControl<TextBlock>("TotalTimeText");
            var btn = this.FindControl<Button>("PlayPauseButton");
            if (_mp == null || seek == null || vol == null || cur == null || tot == null || btn == null) return;

            vol.Value = _mp.Volume;

            _mp.TimeChanged += (_, e) =>
            {
                if (_isSeeking) return;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        long time = (long)e.Time;
                        cur.Text = FormatTime(time);
                        var length = 0L;
                        try { length = _mp?.Media?.Duration ?? _mp?.Length ?? 0; } catch { length = 0; }
                        tot.Text = FormatTime(length);
                        if (length > 0)
                        {
                            seek.Maximum = length;
                            seek.Value = Math.Clamp(time, 0, length);
                        }
                    }
                    catch { }
                });
            };

            _mp.EndReached += (_, __) => Dispatcher.UIThread.Post(() => { try { btn.Content = "Play"; } catch { } });
            _mp.Playing += (_, __) => Dispatcher.UIThread.Post(() => { try { btn.Content = "Pause"; } catch { } });
            _mp.Paused += (_, __) => Dispatcher.UIThread.Post(() => { try { btn.Content = "Play"; } catch { } });
            _mp.Stopped += (_, __) => Dispatcher.UIThread.Post(() => { try { btn.Content = "Play"; } catch { } });
        }
        catch { }
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0) return "00:00";
        var ts = TimeSpan.FromMilliseconds(ms);
    return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void OnPlayPause(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_mp == null) return;
            if (_mp.IsPlaying) _mp.Pause(); else _mp.Play();
        }
        catch { }
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    { _isSeeking = true; }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            var seek = sender as Slider ?? this.FindControl<Slider>("SeekSlider");
            if (_mp != null && seek != null)
            {
                var target = (long)seek.Value;
                _mp.Time = target;
            }
        }
        catch { }
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

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        try { if (_mp != null) _mp.Volume = (int)e.NewValue; } catch { }
    }

    private static void SetGifSource(Image gifControl, string url)
    {
        // AnimatedImage.Avalonia v2 uses an attached property on Image: anim:ImageBehavior.AnimatedSource
        // We can't reference the assembly types directly here; set via reflection on attached property.
        try
        {
            var uri = new Uri(url);
            // Attached property owner type name: ImageBehavior in AnimatedImage.Avalonia
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AnimatedImage.Avalonia");
            var owner = asm?.GetTypes().FirstOrDefault(t => t.Name == "ImageBehavior");
            var propField = owner?.GetField("AnimatedSourceProperty", BindingFlags.Public | BindingFlags.Static);
            if (owner != null && propField?.GetValue(null) is AvaloniaProperty ap)
            {
                gifControl.SetValue(ap, uri);
                return;
            }
            // Fallback: show still image via RemoteImage behavior (works with remote URLs)
            try { RemoteImage.SetSourceUri(gifControl, url); } catch { }
        }
        catch
        {
            try { RemoteImage.SetSourceUri(gifControl, url); } catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            if (_mp != null)
            {
                if (_mp.IsPlaying) _mp.Stop();
                _mp.Dispose();
                _mp = null;
            }
            _libVLC?.Dispose();
            _libVLC = null;
        }
        catch { }
    }

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
