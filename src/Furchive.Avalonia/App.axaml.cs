using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Furchive.Core.Interfaces;
using Furchive.Core.Services;
using Furchive.Core.Platforms;
using Furchive.Avalonia.Views;
using Furchive.Avalonia.ViewModels;
using Furchive.Avalonia.Services;
using Furchive.Avalonia.Infrastructure;
using System.Net.Http;
using Avalonia.Styling;
using System.IO;
using System;

namespace Furchive.Avalonia;

public partial class App : Application
{
    private IHost? _host;
    internal static IServiceProvider? Services { get; private set; }
    private ISettingsService? _settings;
    private ILogger<App>? _logger;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Preload settings just enough to know logging level
        SettingsService? preloadedSettings = null;
        bool debugLogging = false;
        try
        {
            var tempLoggerFactory = LoggerFactory.Create(b => { });
            var tempLogger = tempLoggerFactory.CreateLogger<SettingsService>();
            preloadedSettings = new SettingsService(tempLogger);
            preloadedSettings.LoadAsync().GetAwaiter().GetResult();
            debugLogging = preloadedSettings.GetSetting<bool>("DebugLoggingEnabled", false);
        }
        catch { }

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((context, services) =>
        {
            services.AddSingleton<IConfiguration>(context.Configuration);
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.SetMinimumLevel(debugLogging ? LogLevel.Debug : LogLevel.Information);
                if (debugLogging) logging.AddDebug();
                logging.AddConsole();
                try
                {
                    var logsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs");
                    Directory.CreateDirectory(logsRoot);
                    var debugLog = Path.Combine(logsRoot, "debug.log");
                    try { File.WriteAllText(debugLog, string.Empty); } catch { }
                    logging.AddProvider(new FileLoggerProvider(debugLog));
                }
                catch { }
            });
            services.AddHttpClient();
            if (preloadedSettings != null) services.AddSingleton<ISettingsService>(preloadedSettings); else services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IUnifiedApiService, UnifiedApiService>();
            services.AddSingleton<IDownloadsStore, SqliteDownloadsStore>();
            services.AddSingleton<IDownloadService, DownloadService>();
            services.AddSingleton<ILocalMediaProxy, LocalMediaProxyService>();
            services.AddHostedService(sp => (LocalMediaProxyService)sp.GetRequiredService<ILocalMediaProxy>());
            services.AddSingleton<IThumbnailCacheService, ThumbnailCacheService>();
            services.AddSingleton<ICpuWorkQueue, CpuWorkQueue>();
            services.AddSingleton<IPlatformShellService, PlatformShellService>();
            services.AddSingleton<IPoolsCacheStore, SqlitePoolsCacheStore>();
            services.AddSingleton<IPostsCacheStore, SqlitePostsCacheStore>();
            services.AddSingleton<IE621CacheStore, E621SqliteCacheStore>();
            services.AddSingleton<IPoolPruningService, PoolPruningService>();
            services.AddHostedService(sp => (CpuWorkQueue)sp.GetRequiredService<ICpuWorkQueue>());
            services.AddTransient<IPlatformApi>(sp =>
            {
                var http = sp.GetRequiredService<HttpClient>();
                var logger = sp.GetRequiredService<ILogger<E621Api>>();
                var settings = sp.GetService<ISettingsService>();
                var cache = sp.GetService<IE621CacheStore>();
                return new E621Api(http, logger, settings, cache);
            });
            services.AddTransient<MainViewModel>();
            services.AddTransient<MainWindow>();
        });

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();
        Services = _host.Services;
        _settings = preloadedSettings ?? Services.GetService<ISettingsService>();

        ApplyThemeFromSettings();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var wnd = Services!.GetRequiredService<MainWindow>();
            desktop.MainWindow = wnd;
            if (!wnd.IsVisible) wnd.Show();
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            // Capture logger if available
            try { _logger = Services.GetService<ILogger<App>>(); } catch { }

            // Ensure temp directory cleaned when app exits
            desktop.Exit += (_, __) =>
            {
                try { CleanupTempDirectory(); } catch { }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyThemeFromSettings()
    {
        try
        {
            RequestedThemeVariant = ThemeVariant.Dark;
        }
        catch { }
    }

    private void ApplyIconTheme() { /* no-op simplified */ }

    private void CleanupTempDirectory()
    {
        try
        {
            var tempRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
            if (!Directory.Exists(tempRoot)) return;
            _logger?.LogDebug("Cleaning temp directory at {Path}", tempRoot);

            // Delete files first
            try
            {
                foreach (var file in Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }

            // Then attempt to delete subdirectories (deepest first)
            try
            {
                var dirs = Directory.EnumerateDirectories(tempRoot, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length)
                    .ToList();
                foreach (var d in dirs)
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            catch { }

            // Finally attempt to remove the root itself (optional)
            try { Directory.Delete(tempRoot, true); } catch { }
        }
        catch { }
    }
}
