using System;
using System.IO;
using Avalonia;

namespace Furchive.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Earliest diagnostics: write a marker inside LocalAppData and beside the executable (base directory)
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "startup-trace.log"), $"[{DateTime.Now:O}] Enter Main()\n");
            // Base directory sentinel helps detect when LocalAppData logging is blocked or not reached
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-marker.txt"), $"Started Main() at {DateTime.Now:O}\n"); } catch { }
        }
        catch { }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "startup-fatal.log"), $"[{DateTime.Now:O}] FATAL in Main: {ex}\n");
            }
            catch { }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
