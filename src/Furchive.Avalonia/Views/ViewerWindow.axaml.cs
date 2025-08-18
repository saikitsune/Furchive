using Avalonia.Controls;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Furchive.Core.Interfaces;
using System;
using Avalonia.Input;
using System.Reflection;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    public ViewerWindow()
    {
        InitializeComponent();
        this.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

    // Try to attach WebView at runtime if the assembly is available
    TryAttachWebView();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenOriginal(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.FullImageUrl))
        {
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.FullImageUrl); } catch { }
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.SourceUrl))
        {
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.SourceUrl); } catch { }
        }
    }

    private void TryAttachWebView()
    {
        try
        {
            var host = this.FindControl<Panel>("WebHost");
            var fallback = this.FindControl<Control>("WebFallback");
            if (host == null) return;
            // Load Avalonia.WebView types via reflection so build doesn't require the package
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Avalonia.WebView", StringComparison.OrdinalIgnoreCase));
            if (asm == null)
            {
                fallback?.IsVisible.Equals(true);
                return;
            }
            var webViewType = asm.GetType("Avalonia.WebView.WebView2");
            if (webViewType == null)
            {
                fallback?.IsVisible.Equals(true);
                return;
            }
            var web = Activator.CreateInstance(webViewType) as Control;
            if (web == null)
            {
                fallback?.IsVisible.Equals(true);
                return;
            }
            // Set Source property if exists and DataContext has FullImageUrl
            var srcProp = webViewType.GetProperty("Source");
            if (srcProp != null && DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.FullImageUrl))
            {
                srcProp.SetValue(web, m.FullImageUrl);
            }
            host.Children.Clear();
            host.Children.Add(web);
            if (fallback != null) fallback.IsVisible = false;
        }
        catch { }
    }

    private void OnImageWheel(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            var slider = this.FindControl<Slider>("ZoomSlider");
            if (slider == null) return;
            var delta = e.Delta.Y > 0 ? 0.1 : -0.1;
            var next = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            slider.Value = next;
            e.Handled = true;
        }
        catch { }
    }
}
