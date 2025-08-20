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
using Furchive.Avalonia.Infrastructure;
using System.Net.Http;
using Avalonia.Styling;

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
        Window? placeholder = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime preDesktop)
        {
            try
            {
        // Prevent the app from shutting down when closing the placeholder by requiring explicit shutdown during splash
        preDesktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] ShutdownMode set to OnExplicitShutdown before showing placeholder\n"); } catch { }
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Creating placeholder window...\n"); } catch { }
                placeholder = new Window { Title = "Furchive", Width = 400, Height = 300, Content = new TextBlock { Text = "Launching…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                placeholder.Show();
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Placeholder window shown\n"); } catch { }
            }
            catch (Exception ex)
            {
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] Placeholder failed: {ex}\n"); } catch { }
            }
        }

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
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] MainWindow resolved from DI\n"); } catch { }
                desktop.MainWindow = wnd;
                logger?.LogInformation("MainWindow created and assigned.");
                try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] MainWindow assigned to desktop lifetime\n"); } catch { }
                // Show the main window before switching shutdown mode back and closing placeholder
                try { if (!wnd.IsVisible) wnd.Show(); } catch { }
                // Switch to normal shutdown behavior after MainWindow is visible
                try
                {
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] ShutdownMode set to OnLastWindowClose after showing MainWindow\n");
                }
                catch { }
                try { placeholder?.Close(); } catch { }
            }
            catch (Exception ex)
            {
                try
                {
                    var logger = Services?.GetService<ILogger<App>>();
                    logger?.LogError(ex, "Failed to create or assign MainWindow");
                    try { if (!string.IsNullOrEmpty(traceFile)) File.AppendAllText(traceFile, $"[{DateTime.Now:O}] EXCEPTION creating/assigning MainWindow: {ex}\n"); } catch { }
                }
                catch { }
                // Show a minimal fallback window to surface the error instead of silently hanging
                try
                {
                    var tb = new TextBlock { Text = ex.ToString(), TextWrapping = TextWrapping.Wrap };
                    var sv = new ScrollViewer { Content = tb, Margin = new Thickness(12) };
                    var fb = new Window { Title = "Furchive - Startup Error", Width = 900, Height = 600, Content = sv };
                    desktop.MainWindow = fb;
                    fb.Show();
                }
                catch { }
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
