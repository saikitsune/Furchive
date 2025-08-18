using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

namespace Furchive.Avalonia;

public partial class App : Application
{
    private IHost? _host;
    internal static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((context, services) =>
        {
            services.AddSingleton<IConfiguration>(context.Configuration);
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
                logging.AddConsole();
                var debugDir = Path.Combine(AppContext.BaseDirectory, "debug");
                var debugLog = Path.Combine(debugDir, "debug.log");
                try { Directory.CreateDirectory(debugDir); File.WriteAllText(debugLog, string.Empty); } catch { }
                logging.AddProvider(new FileLoggerProvider(debugLog));
            });
            services.AddHttpClient();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IUnifiedApiService, UnifiedApiService>();
            services.AddSingleton<IDownloadService, DownloadService>();
            services.AddSingleton<IThumbnailCacheService, ThumbnailCacheService>();
            services.AddSingleton<ICpuWorkQueue, CpuWorkQueue>();
            services.AddSingleton<IPlatformShellService, PlatformShellService>();
            services.AddHostedService(sp => (CpuWorkQueue)sp.GetRequiredService<ICpuWorkQueue>());
            services.AddTransient<IPlatformApi>(sp =>
            {
                var http = sp.GetRequiredService<HttpClient>();
                var logger = sp.GetRequiredService<ILogger<E621Api>>();
                var settings = sp.GetService<ISettingsService>();
                return new E621Api(http, logger, settings);
            });
            services.AddTransient<Furchive.Avalonia.ViewModels.MainViewModel>();
            services.AddTransient<MainWindow>();
        });
        _host = builder.Build();
        await _host.StartAsync();
        Services = _host.Services;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                var logger = Services.GetService<ILogger<App>>();
                var wnd = Services.GetRequiredService<MainWindow>();
                desktop.MainWindow = wnd;
                logger?.LogInformation("MainWindow created and assigned.");
            }
            catch (Exception ex)
            {
                try
                {
                    var logger = Services?.GetService<ILogger<App>>();
                    logger?.LogError(ex, "Failed to create or assign MainWindow");
                }
                catch { }
                // Rethrow so the process exits with a clear error instead of silently closing
                throw;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
