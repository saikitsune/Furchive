using System;
using System.Configuration;
using System.Data;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Furchive.Core.Interfaces;
using Furchive.Core.Services;
using Furchive.ViewModels;
using Furchive.Views;
using Furchive.Core.Platforms;
using ModernWpf;
using Furchive.Infrastructure;
using System.Net.Http;

namespace Furchive;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private IHost? _host;
    internal static IServiceProvider? Services { get; private set; }
    private string? _startupLogPath;

    private void LogStartup(string message)
    {
        try
        {
            _startupLogPath ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "startup.log");
            var dir = System.IO.Path.GetDirectoryName(_startupLogPath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(_startupLogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging errors
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            // Ensure the app shuts down when the main window closes
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // Hook unhandled exception handlers early to catch startup issues
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            LogStartup("Starting host builder...");
            var builder = Host.CreateDefaultBuilder(e.Args);

            builder.ConfigureServices((context, services) =>
            {
                // Configuration
                services.AddSingleton<IConfiguration>(context.Configuration);

                // Logging
                // Use Debug and Console providers; attach a console window when possible
                services.AddLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddDebug();
                    logging.AddConsole();
                    // File logger to /debug/debug.log
                    var debugDir = System.IO.Path.Combine(AppContext.BaseDirectory, "debug");
                    var debugLog = System.IO.Path.Combine(debugDir, "debug.log");
                    try
                    {
                        System.IO.Directory.CreateDirectory(debugDir);
                        // Clear on startup by truncating - will be re-opened by provider per log call
                        System.IO.File.WriteAllText(debugLog, string.Empty);
                    }
                    catch { }
                    logging.AddProvider(new FileLoggerProvider(debugLog));
                });

                // HTTP Client
                services.AddHttpClient();

                // Core Services
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton<IUnifiedApiService, UnifiedApiService>();
                services.AddSingleton<IDownloadService, DownloadService>();
                services.AddSingleton<IThumbnailCacheService, ThumbnailCacheService>();
                services.AddSingleton<ICpuWorkQueue, CpuWorkQueue>();
                services.AddHostedService(sp => (CpuWorkQueue)sp.GetRequiredService<ICpuWorkQueue>());

                // Platform APIs (will be registered in MainViewModel)
                services.AddTransient<IPlatformApi>(sp =>
                {
                    var http = sp.GetRequiredService<HttpClient>();
                    var logger = sp.GetRequiredService<ILogger<E621Api>>();
                    var settings = sp.GetService<ISettingsService>();
                    return new E621Api(http, logger, settings);
                });
                // Removed other platforms (FurAffinity, InkBunny, Weasyl) to focus on e621 only

                // ViewModels
                services.AddTransient<MainViewModel>();
                services.AddTransient<SettingsViewModel>();
                // services.AddTransient<GalleryViewModel>();
                // services.AddTransient<DownloadViewModel>();

                // Views
                services.AddTransient<MainWindow>();
                services.AddTransient<SettingsWindow>();
                services.AddTransient<ViewerWindow>();
            });

            LogStartup("Building host...");
            _host = builder.Build();
            LogStartup("Starting host...");
            await _host.StartAsync();
            Services = _host.Services;

            // In Release, don't attach a console to avoid a separate window.
            // If desired during development, this remains enabled for DEBUG builds only.
#if DEBUG
            try { NativeConsole.Attach(); } catch { }
#endif

            // Initialize settings
            LogStartup("Loading settings...");
            var settingsService = _host.Services.GetRequiredService<ISettingsService>();
            await settingsService.LoadAsync();
            LogStartup("Settings loaded.");

            // Ensure defaults for new background worker settings
            try
            {
                if (settingsService.GetSetting<int>("CpuWorkerDegree", -1) <= 0)
                {
                    var def = Math.Clamp(Environment.ProcessorCount / 2, 1, Environment.ProcessorCount);
                    await settingsService.SetSettingAsync("CpuWorkerDegree", def);
                }
                // If not present, initialize ThumbnailPrewarmEnabled to true
                var hasPrewarm = settingsService.GetSetting<string>("ThumbnailPrewarmEnabled", null);
                if (string.IsNullOrEmpty(hasPrewarm)) await settingsService.SetSettingAsync("ThumbnailPrewarmEnabled", true);
            }
            catch { }


            // Clean temp viewer folder on startup
            try
            {
                var tempDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
                if (System.IO.Directory.Exists(tempDir))
                {
                    foreach (var f in System.IO.Directory.EnumerateFiles(tempDir))
                    {
                        try { System.IO.File.Delete(f); } catch { }
                    }
                }
            }
            catch { }

            // Apply theme per settings (default: follow Windows)
            var themeMode = settingsService.GetSetting<string>("ThemeMode", "system")?.ToLowerInvariant() ?? "system";
            ApplyTheme(themeMode);

            // Show main window
            LogStartup("Resolving MainWindow...");
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            // Now that MainWindow and its ViewModel are constructed (platforms registered), load persistent caches
            try
            {
                var unified = _host.Services.GetRequiredService<IUnifiedApiService>();
                (unified as dynamic)?.LoadE621PersistentCacheIfEnabled();
                LogStartup("Loaded persistent caches (if enabled).");
            }
            catch (Exception ex)
            {
                LogStartup($"Persistent cache load failed: {ex.Message}");
            }
            LogStartup("MainWindow resolved. Showing window...");
            mainWindow.Show();
            LogStartup("MainWindow shown.");

            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            LogStartup($"Startup exception: {ex}\nStackTrace: {ex.StackTrace}");
            System.Windows.MessageBox.Show($"Error starting application: {ex.Message}\n\nStack trace:\n{ex.StackTrace}", 
                          "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            try { Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}"); } catch {}
            System.Windows.MessageBox.Show($"Unobserved task exception: {e.Exception.GetBaseException().Message}\n\n{e.Exception}",
                "Unhandled Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            e.SetObserved();
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
    try { Console.Error.WriteLine($"[UnhandledException] {ex}"); } catch {}
    System.Windows.MessageBox.Show($"Unhandled domain exception: {ex?.GetBaseException().Message}\n\n{ex}",
            "Unhandled Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
    try { Console.Error.WriteLine($"[DispatcherUnhandledException] {e.Exception}"); } catch {}
    System.Windows.MessageBox.Show($"Unhandled UI exception: {e.Exception.GetBaseException().Message}\n\n{e.Exception}",
            "Unhandled Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void ApplyTheme(string themeMode)
    {
        try
        {
            if (string.Equals(themeMode, "system", StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.Current.ApplicationTheme = null; // follow system
            }
            else if (string.Equals(themeMode, "light", StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
            }
            else if (string.Equals(themeMode, "dark", StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
            }
        }
        catch
        {
            // ignore theme errors
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Clean temp viewer folder on exit
        try
        {
            var tempDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "temp");
            if (System.IO.Directory.Exists(tempDir))
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(tempDir))
                {
                    try { System.IO.File.Delete(f); } catch { }
                }
            }
        }
        catch { }

        if (_host != null)
        {
            // Save persistent API caches if enabled
            try
            {
                var unified = _host.Services.GetRequiredService<IUnifiedApiService>();
                (unified as dynamic)?.SaveE621PersistentCacheIfEnabled();
            }
            catch { }

            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}

internal static class NativeConsole
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    public static void Attach()
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                AllocConsole();
            }
        }
        catch
        {
            // ignore
        }
    }
}
