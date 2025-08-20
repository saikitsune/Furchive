using System;
using Avalonia;
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
            // Avalonia.WebView uses platform detect; no explicit setup required here for Desktop.
            .LogToTrace();
}
