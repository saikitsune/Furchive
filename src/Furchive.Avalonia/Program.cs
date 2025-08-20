using System;
using Avalonia;
#if HAS_WEBVIEW_AVALONIA
using Avalonia.WebView.Desktop; // enables UseDesktopWebView()
#endif

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
#if HAS_WEBVIEW_AVALONIA
            .UseDesktopWebView()
#endif
            .LogToTrace();
}
