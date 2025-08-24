using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Bmp;
using SharpImage = SixLabors.ImageSharp.Image;
using AvImage = global::Avalonia.Controls.Image;

namespace Furchive.Avalonia.Controls;

// Service that animates a local GIF within an existing Avalonia Image control.
public static class GifAnimatorService
{
    private sealed class AnimationState
    {
        public CancellationTokenSource Cts { get; } = new();
    public List<(Bitmap frame, int delayMs)> Frames { get; } = new();
    public ManualResetEventSlim ReadyForAnimation { get; } = new(false);
    public Task? AnimatorTask { get; set; }
    }

    private static readonly ConcurrentDictionary<AvImage, AnimationState> _states = new();

    public static void Start(AvImage target, string filePath)
    {
        Stop(target);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        // Basic guard: only handle .gif (case-insensitive)
        try
        {
            if (!filePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch { }
    var state = new AnimationState();
    _states[target] = state;
    LogGif($"gif-start path={filePath}");
    // Kick off decoder and animator separately so we can animate while decoding remaining frames
    state.AnimatorTask = Task.Run(() => AnimationLoopAsync(target, state));
    _ = Task.Run(() => DecodeFramesAsync(target, filePath, state));
    }

    public static void Stop(AvImage target)
    {
        if (_states.TryRemove(target, out var st))
        {
            try { st.Cts.Cancel(); } catch { }
            foreach (var (frame, _) in st.Frames)
            {
                try { frame.Dispose(); } catch { }
            }
            st.Frames.Clear();
        }
    }

    private static async Task DecodeFramesAsync(AvImage target, string path, AnimationState state)
    {
        var ct = state.Cts.Token;
        try
        {
            using var image = await SharpImage.LoadAsync<Rgba32>(path, ct);
            // Extract frames + delays
            int frameCount = image.Frames.Count;
            if (frameCount == 0) return;
            LogGif($"gif-decode path={path} frames={frameCount}");
            for (int i = 0; i < frameCount; i++)
            {
                if (ct.IsCancellationRequested) return;
                var frame = image.Frames.CloneFrame(i);
                int delayCs = 10; // default
                // Attempt to reflect delay from metadata (ImageSharp version differences)
                try
                {
                    var metaObj = frame.Metadata; // ImageFrameMetadata
                    var gifMetaProp = metaObj.GetType().GetProperty("Gif");
                    var gifMeta = gifMetaProp?.GetValue(metaObj);
                    if (gifMeta != null)
                    {
                        var frameDelayProp = gifMeta.GetType().GetProperty("FrameDelay");
                        if (frameDelayProp?.GetValue(gifMeta) is int fd && fd > 0)
                            delayCs = fd;
                    }
                }
                catch { }
                // Convert frame to Bitmap using fast BMP (no heavy compression) to reduce startup latency
                try
                {
                    using var ms = new MemoryStream();
                    await frame.SaveAsync(ms, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel32 });
                    ms.Position = 0;
                    var bmp = new Bitmap(ms);
                    state.Frames.Add((bmp, Math.Max(20, delayCs * 10))); // clamp to >=20ms
                }
                catch { }
                finally { frame.Dispose(); }

                // Display first frame immediately
                if (state.Frames.Count == 1)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try { target.Source = state.Frames[0].frame; } catch { }
                    });
                }
                // Signal animation readiness after second frame available
                if (state.Frames.Count == 2 && !state.ReadyForAnimation.IsSet)
                {
                    state.ReadyForAnimation.Set();
                }
            }
            // If only one frame ultimately, ensure animation loop doesn't wait forever
            if (state.Frames.Count == 1 && !state.ReadyForAnimation.IsSet)
                state.ReadyForAnimation.Set();
        }
        catch (Exception ex)
        {
            LogGif($"gif-error path={path} msg={ex.Message}");
        }
    }

    private static async Task AnimationLoopAsync(AvImage target, AnimationState state)
    {
        var ct = state.Cts.Token;
        try
        {
            // Wait until at least 2 frames (or single-frame GIF) ready or cancellation
            state.ReadyForAnimation.Wait(ct);
        }
        catch { }
        // Loop only if more than one frame; single frame already displayed
        if (ct.IsCancellationRequested || state.Frames.Count <= 1) return;
        int idx = 0;
        int loops = 0;
        while (!ct.IsCancellationRequested)
        {
            if (state.Frames.Count == 0) break;
            var frameTuple = state.Frames[Math.Min(idx, state.Frames.Count - 1)];
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                try { target.Source = frameTuple.frame; } catch { }
            });
            try { await Task.Delay(frameTuple.delayMs, ct); } catch { }
            idx++;
            if (idx >= state.Frames.Count)
            {
                idx = 0; loops++;
                if (loops % 10 == 0) LogGif($"gif-loop loops={loops}");
            }
        }
    }

    private static void LogGif(string line)
    {
        try
        {
            static bool IsEnabled() => string.Equals(Environment.GetEnvironmentVariable("FURCHIVE_DEBUG_GIF"), "1", StringComparison.Ordinal);
            bool isError = line.Contains("error", StringComparison.OrdinalIgnoreCase);
            if (!IsEnabled() && !isError) return;
            var logsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
            Directory.CreateDirectory(logsRoot);
            File.AppendAllText(Path.Combine(logsRoot, "viewer.log"), $"[{DateTime.Now:O}] {line}\n");
        }
        catch { }
    }
}
