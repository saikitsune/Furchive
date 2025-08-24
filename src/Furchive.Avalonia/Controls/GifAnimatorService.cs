using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;
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
    }

    private static readonly ConcurrentDictionary<AvImage, AnimationState> _states = new();

    public static void Start(AvImage target, string filePath)
    {
        Stop(target);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;
        var state = new AnimationState();
        _states[target] = state;
        _ = Task.Run(() => DecodeAndAnimateAsync(target, filePath, state));
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

    private static async Task DecodeAndAnimateAsync(AvImage target, string path, AnimationState state)
    {
        var ct = state.Cts.Token;
        try
        {
            using var image = await SharpImage.LoadAsync<Rgba32>(path, ct);
            // Extract frames + delays
            int frameCount = image.Frames.Count;
            if (frameCount == 0) return;
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
                // Convert frame to Bitmap (PNG encoding for simplicity)
                try
                {
                    using var ms = new MemoryStream();
                    await frame.SaveAsync(ms, new PngEncoder());
                    ms.Position = 0;
                    var bmp = new Bitmap(ms);
                    state.Frames.Add((bmp, Math.Max(20, delayCs * 10))); // clamp to >=20ms
                }
                catch { }
                finally { frame.Dispose(); }
            }

            // Show first frame immediately (UI thread)
            if (state.Frames.Count > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    try { target.Source = state.Frames[0].frame; } catch { }
                });
            }

            // Animation loop (respect GIF global repeat count; 0=infinite)
            int repeat = 0; // Unknown repeat -> treat as infinite
            int loops = 0;
            while (!ct.IsCancellationRequested && (repeat == 0 || loops <= repeat))
            {
                for (int i = 0; i < state.Frames.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var (bmp, delayMs) = state.Frames[i];
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try { target.Source = bmp; } catch { }
                    });
                    try { await Task.Delay(delayMs, ct); } catch { }
                }
                loops++;
            }
        }
        catch { }
    }
}
