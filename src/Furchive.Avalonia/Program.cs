using System;
using Avalonia;
// Note: Avalonia.WebView doesn't provide UseWindowsWebView2 extension for AppBuilder in this version.
// using Avalonia.ReactiveUI;

namespace Furchive.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Avalonia.WebView initializes via platform detect in this setup; no extra call here.
            .LogToTrace();
}
