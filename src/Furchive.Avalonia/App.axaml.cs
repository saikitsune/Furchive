using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Furchive.Core.Interfaces;
using Furchive.Core.Services;
using Furchive.Core.Platforms;
using Furchive.Avalonia.Views;
using Furchive.Avalonia.ViewModels;
// Removed WebView infrastructure usage (migrated to LibVLC for video)
using Furchive.Avalonia.Services;
using Furchive.Avalonia.Infrastructure; // for FileLoggerProvider
using System.Net.Http;
using Avalonia.Styling;
// (WebView explicit builder namespace not present in current package build)

namespace Furchive.Avalonia;

public partial class App : Application
{
    private IHost? _host;
    internal static IServiceProvider? Services { get; private set; }
    private ISettingsService? _settings;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
    try
    {
        // Simple file trace to diagnose startup even if regular logging fails
        string traceFile = string.Empty;
        try
        {
            var logsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
            Directory.CreateDirectory(logsRoot);
            traceFile = Path.Combine(logsRoot, "startup-trace.log");
            File.WriteAllText(traceFile, $"[{DateTime.Now:O}] Enter OnFrameworkInitializationCompleted\n");
        }
        catch { }

    // WebView initialization path removed.

    var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((context, services) =>
        {
            services.AddSingleton<IConfiguration>(context.Configuration);
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddConsole();
                // Write logs to a user-writable folder to avoid permission issues under Program Files
                try
                {
                    var logsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
                    Directory.CreateDirectory(logsRoot);
                    var debugLog = Path.Combine(logsRoot, "debug.log");
                    // Truncate on each run to keep the file small
                    try { File.WriteAllText(debugLog, string.Empty); } catch { }
                    // Also truncate viewer and vlc logs at app startup for fresh diagnostics each run
                    try { File.WriteAllText(Path.Combine(logsRoot, "viewer.log"), string.Empty); } catch { }
                    try { File.WriteAllText(Path.Combine(logsRoot, "vlc.log"), string.Empty); } catch { }
                    logging.AddProvider(new FileLoggerProvider(debugLog));
                }
                catch
                {
                    // If logging cannot initialize, continue with default providers only
                }
            });
            services.AddHttpClient();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IUnifiedApiService, UnifiedApiService>();
            services.AddSingleton<IDownloadService, DownloadService>();
            // Local media proxy to serve HTML5 player and proxy video with headers
            services.AddSingleton<ILocalMediaProxy, LocalMediaProxyService>();
            services.AddHostedService(sp => (LocalMediaProxyService)sp.GetRequiredService<ILocalMediaProxy>());
            services.AddSingleton<IThumbnailCacheService, ThumbnailCacheService>();
            services.AddSingleton<ICpuWorkQueue, CpuWorkQueue>();
            services.AddSingleton<IPlatformShellService, PlatformShellService>();
            services.AddSingleton<IPoolsCacheStore, SqlitePoolsCacheStore>();
            services.AddSingleton<IE621CacheStore, E621SqliteCacheStore>();
            services.AddHostedService(sp => (CpuWorkQueue)sp.GetRequiredService<ICpuWorkQueue>());
            services.AddTransient<IPlatformApi>(sp =>
            {
                var http = sp.GetRequiredService<HttpClient>();
                var logger = sp.GetRequiredService<ILogger<E621Api>>();
                var settings = sp.GetService<ISettingsService>();
                var cache = sp.GetService<IE621CacheStore>();
                return new E621Api(http, logger, settings, cache);
            });
            services.AddTransient<Furchive.Avalonia.ViewModels.MainViewModel>();
            services.AddTransient<MainWindow>();
        });
    _host = builder.Build();
    try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Host built\n"); } catch { }
        // Start host synchronously to avoid returning before MainWindow is created
        _host.StartAsync().GetAwaiter().GetResult();
        Services = _host.Services;
    try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Host started\n"); } catch { }
        try
        {
            if (!string.IsNullOrEmpty(traceFile))
            {
                var lt = ApplicationLifetime != null ? ApplicationLifetime.GetType().FullName : "null";
                File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Lifetime after host start: {lt}\n");
            }
        }
        catch { }

    // Optionally show a tiny placeholder window early to prove GUI can show while we continue init
    // Remove placeholder logic to avoid blocking startup

    // Ensure settings are loaded from disk before creating any windows/view models
        try
        {
            var sp = Services;
            if (sp != null)
            {
        _settings = sp.GetService<ISettingsService>();
        if (_settings != null)
                {
                    try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Settings load starting\n"); } catch { }
                    // Load settings synchronously to guarantee availability
            _settings.LoadAsync().GetAwaiter().GetResult();
                    try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Settings loaded\n"); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            try
            {
                if (Services is IServiceProvider sp)
                {
                    var logger = sp.GetService<ILogger<App>>();
                    logger?.LogError(ex, "Failed to load settings at startup");
                }
            }
            catch { }
        }

        try
        {
            if (!string.IsNullOrEmpty(traceFile))
            {
                var lt2 = ApplicationLifetime != null ? ApplicationLifetime.GetType().FullName : "null";
                File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Lifetime: {lt2}\n");
            }
        }
        catch { }

        // Apply theme before creating main window, and subscribe for live updates
        try
        {
            ApplyThemeFromSettings();
            if (_settings != null)
            {
                _settings.SettingChanged += OnSettingChanged;
            }
        }
        catch { }

    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var sp = _host!.Services;
                var logger = sp.GetService<ILogger<App>>();
                var wnd = sp.GetRequiredService<MainWindow>();
                desktop.MainWindow = wnd;
                logger?.LogInformation("MainWindow created and assigned.");
                if (!wnd.IsVisible) wnd.Show();
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                try
                {
                    desktop.Exit += (_, __) =>
                    {
                        try
                        {
                            var tempDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
                            if (Directory.Exists(tempDir))
                            {
                                foreach (var file in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
                                {
                                    try { File.Delete(file); } catch { }
                                }
                                // Optionally remove empty subdirectories
                                foreach (var dir in Directory.EnumerateDirectories(tempDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                                {
                                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
                                }
                            }
                        }
                        catch { }
                    };
                }
                catch { }
                if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] MainWindow shown and shutdown mode set\n");
            }
            catch (Exception ex)
            {
                var logger = Services?.GetService<ILogger<App>>();
                logger?.LogError(ex, "Failed to create or assign MainWindow");
                if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] EXCEPTION creating/assigning MainWindow: {ex}\n");
                // Show a minimal fallback window to surface the error instead of silently hanging
                var tb = new TextBlock { Text = ex.ToString(), TextWrapping = TextWrapping.Wrap };
                var sv = new ScrollViewer { Content = tb, Margin = new Thickness(12) };
                var fb = new Window { Title = "Furchive - Startup Error", Width = 900, Height = 600, Content = sv };
                desktop.MainWindow = fb;
                fb.Show();
                return;
            }
        }
        else
        {
            // Fallback: try to show a window even if lifetime isn't classic, for diagnostics
            try
            {
                var sp = _host!.Services;
                var wnd = sp.GetRequiredService<MainWindow>();
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Non-classic lifetime; calling Show() on MainWindow\n"); } catch { }
                wnd.Show();
            }
            catch (Exception ex)
            {
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] EXCEPTION in non-classic Show(): {ex}\n"); } catch { }
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
        try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Exiting OnFrameworkInitializationCompleted\n"); } catch { }
        }
        catch (Exception ex)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "startup-fatal.txt");
                File.WriteAllText(path, ex.ToString());
            }
            catch { }
            throw;
        }
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (!string.Equals(key, "ThemeMode", StringComparison.OrdinalIgnoreCase)) return;
        try { ApplyThemeFromSettings(); } catch { }
    }

    private void ApplyThemeFromSettings()
    {
        var mode = _settings?.GetSetting<string>("ThemeMode", "system")?.Trim().ToLowerInvariant() ?? "system";
        // System means follow OS; in Avalonia set RequestedThemeVariant to Default
        var variant = mode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        try
        {
            RequestedThemeVariant = variant;
        }
        catch { }
        // Also update all open windows immediately
        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.Windows != null)
            {
                foreach (var w in desktop.Windows)
                {
                    try { w.RequestedThemeVariant = variant; } catch { }
                }
            }
        }
        catch { }
    }
}
