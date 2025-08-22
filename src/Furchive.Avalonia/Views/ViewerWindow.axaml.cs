using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml;
using Furchive.Core.Models;
using Furchive.Avalonia.Behaviors;

namespace Furchive.Avalonia.Views;

// Minimal viewer: on open, display the media item's image (FullImageUrl preferred, else PreviewUrl).
public partial class ViewerWindow : Window
{
    public ViewerWindow()
    {
        InitializeComponent();
        Opened += (_, _) => LoadMedia();
    }

    private void LoadMedia()
    {
        if (DataContext is not MediaItem item) return;
        try { Title = string.IsNullOrWhiteSpace(item.Title) ? "Viewer" : item.Title; } catch { }
        var img = this.FindControl<Image>("ImageElement");
        if (img == null) return;
        var url = string.IsNullOrWhiteSpace(item.FullImageUrl) ? item.PreviewUrl : item.FullImageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        // Try local file direct load if path exists, else use RemoteImage to fetch.
        try
        {
            if (File.Exists(url))
            {
                using var fs = File.OpenRead(url);
                img.Source = new Bitmap(fs);
                return;
            }
        }
        catch { }
        try { RemoteImage.SetSourceUri(img, url); } catch { }
    }
}

