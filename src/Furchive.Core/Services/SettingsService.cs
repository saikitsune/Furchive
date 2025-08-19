using Furchive.Core.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Furchive.Core.Services;

/// <summary>
/// Settings service implementation with JSON file persistence
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ConcurrentDictionary<string, object> _settings = new();
    private readonly string _settingsPath;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public event EventHandler<string>? SettingChanged;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
        
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataPath, "Furchive");
        Directory.CreateDirectory(appFolder);
        
        _settingsPath = Path.Combine(appFolder, "settings.json");
    }

    public T? GetSetting<T>(string key, T? defaultValue = default)
    {
        if (_settings.TryGetValue(key, out var value))
        {
            try
            {
                if (value is JsonElement jsonElement)
                {
                    return JsonSerializer.Deserialize<T>(jsonElement.GetRawText());
                }
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert setting {Key} to type {Type}", key, typeof(T));
                return defaultValue;
            }
        }
        return defaultValue;
    }

    public async Task SetSettingAsync<T>(string key, T value)
    {
        var oldValue = _settings.TryGetValue(key, out var existing) ? existing : null;
        
        _settings[key] = value!;
        
        // Save to file
        await SaveAsync().ConfigureAwait(false);
        
        // Notify listeners if value changed
        if (!Equals(oldValue, value))
        {
            SettingChanged?.Invoke(this, key);
        }
        
        _logger.LogDebug("Setting {Key} updated to {Value}", key, value);
    }

    public Dictionary<string, object> GetAllSettings()
    {
        return _settings.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public async Task SaveAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            
            await File.WriteAllTextAsync(_settingsPath, json).ConfigureAwait(false);
            _logger.LogDebug("Settings saved to {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _settingsPath);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task LoadAsync()
    {
        await _fileLock.WaitAsync().ConfigureAwait(false);
        var releasedEarly = false;

        try
        {
            if (!File.Exists(_settingsPath))
            {
                // Release before initializing defaults to avoid deadlock (SaveAsync waits for this lock)
                releasedEarly = true;
                _fileLock.Release();
                await InitializeDefaultSettingsAsync().ConfigureAwait(false);
                return;
            }

            var json = await File.ReadAllTextAsync(_settingsPath).ConfigureAwait(false);
            var loadedSettings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            if (loadedSettings != null)
            {
                _settings.Clear();
                foreach (var kvp in loadedSettings)
                {
                    _settings[kvp.Key] = kvp.Value;
                }
            }

            _logger.LogInformation("Settings loaded from {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings from {Path}, using defaults", _settingsPath);
            if (!releasedEarly)
            {
                releasedEarly = true;
                _fileLock.Release();
            }
            await InitializeDefaultSettingsAsync().ConfigureAwait(false);
        }
        finally
        {
            if (!releasedEarly)
            {
                _fileLock.Release();
            }
        }
    }

    private async Task InitializeDefaultSettingsAsync()
    {
        var defaults = new Dictionary<string, object>
        {
            // Download Settings
            ["DefaultDownloadDirectory"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive"),
            ["FilenameTemplate"] = "{source}/{artist}/{id}.{ext}",
            ["PoolFilenameTemplate"] = "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}",
            ["ConcurrentDownloads"] = 3,
            ["MaxResultsPerSource"] = 50,
            ["RatingsDefault"] = "safe",
            ["DownloadDuplicatesPolicy"] = "skip",
            ["NetworkTimeoutSeconds"] = 30,
            ["RetryAttempts"] = 3,
            ["RateLimitBackoffStrategy"] = "exponential",
            ["SaveMetadataJson"] = false,
            ["CreateSubfoldersBySource"] = true,
            ["UseOriginalFilename"] = false,
            
            // UI Settings
            ["ShowDownloadCompletedToast"] = true,
            ["EnableTagAutocomplete"] = true,
            
            // Authentication (empty by default)
            ["E621Username"] = "",
            ["E621ApiKey"] = "",
            ["ThemeMode"] = "system",
            
            // Content Filtering
            ["TagBlacklist"] = new List<string>()
        };

        foreach (var setting in defaults)
        {
            _settings[setting.Key] = setting.Value;
        }

    await SaveAsync().ConfigureAwait(false);
        _logger.LogInformation("Default settings initialized");
    }

    public void Dispose()
    {
        _fileLock?.Dispose();
    }
}
