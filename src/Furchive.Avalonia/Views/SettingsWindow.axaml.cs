using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Furchive.Avalonia.Messages;

namespace Furchive.Avalonia.Views;

public partial class SettingsWindow : Window
{
    private readonly ISettingsService? _settings;

    public SettingsWindow()
    {
        InitializeComponent();
        _settings = App.Services?.GetService<ISettingsService>();
        LoadValues();
    }

    private void LoadValues()
    {
        var fallback = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
    DownloadDir.Text = _settings?.GetSetting<string>("DefaultDownloadDirectory", fallback) ?? fallback;
    E621User.Text = _settings?.GetSetting<string>("E621Username", "") ?? "";
    E621Key.Text = _settings?.GetSetting<string>("E621ApiKey", "") ?? "";
    CustomUserAgent.Text = _settings?.GetSetting<string>("CustomUserAgent", "") ?? "";
    FilenameTemplate.Text = _settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}";
    PoolFilenameTemplate.Text = _settings?.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}";
    try { PrefetchPagesAhead.Value = _settings?.GetSetting<int>("E621SearchPrefetchPagesAhead", 2) ?? 2; } catch { PrefetchPagesAhead.Value = 2; }
    try { PrefetchParallelism.Value = _settings?.GetSetting<int>("E621SearchPrefetchParallelism", 2) ?? 2; } catch { PrefetchParallelism.Value = 2; }
    try { ConcurrentDownloads.Value = _settings?.GetSetting<int>("ConcurrentDownloads", 3) ?? 3; } catch { ConcurrentDownloads.Value = 3; }
    DownloadDuplicatesPolicy.Text = _settings?.GetSetting<string>("DownloadDuplicatesPolicy", "skip") ?? "skip";
    try { NetworkTimeoutSeconds.Value = _settings?.GetSetting<int>("NetworkTimeoutSeconds", 30) ?? 30; } catch { NetworkTimeoutSeconds.Value = 30; }
    try { MaxResultsPerSource.Value = _settings?.GetSetting<int>("MaxResultsPerSource", 50) ?? 50; } catch { MaxResultsPerSource.Value = 50; }
    try { CpuWorkerDegree.Value = _settings?.GetSetting<int>("CpuWorkerDegree", Math.Max(1, Environment.ProcessorCount / 2)) ?? Math.Max(1, Environment.ProcessorCount / 2); } catch { CpuWorkerDegree.Value = Math.Max(1, Environment.ProcessorCount / 2); }
    try { PoolsUpdateIntervalMinutes.Value = _settings?.GetSetting<int>("PoolsUpdateIntervalMinutes", 360) ?? 360; } catch { PoolsUpdateIntervalMinutes.Value = 360; }
    try { ThumbnailPrewarmEnabled.IsChecked = _settings?.GetSetting<bool>("ThumbnailPrewarmEnabled", true) ?? true; } catch { ThumbnailPrewarmEnabled.IsChecked = true; }
    try { SaveMetadataJson.IsChecked = _settings?.GetSetting<bool>("SaveMetadataJson", false) ?? false; } catch { SaveMetadataJson.IsChecked = false; }
    try { UseOriginalFilename.IsChecked = _settings?.GetSetting<bool>("UseOriginalFilename", false) ?? false; } catch { UseOriginalFilename.IsChecked = false; }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DownloadDir.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621Username", E621User.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621ApiKey", E621Key.Text ?? string.Empty);
            await _settings.SetSettingAsync("CustomUserAgent", CustomUserAgent.Text ?? string.Empty);
            await _settings.SetSettingAsync("FilenameTemplate", FilenameTemplate.Text ?? string.Empty);
            await _settings.SetSettingAsync("PoolFilenameTemplate", PoolFilenameTemplate.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621SearchPrefetchPagesAhead", (int)(PrefetchPagesAhead.Value ?? 2));
            await _settings.SetSettingAsync("E621SearchPrefetchParallelism", (int)(PrefetchParallelism.Value ?? 2));
            // Basic validation
            var policy = (DownloadDuplicatesPolicy.Text ?? "skip").Trim().ToLowerInvariant();
            if (policy != "skip" && policy != "overwrite") policy = "skip";
            await _settings.SetSettingAsync("ConcurrentDownloads", Math.Clamp((int)(ConcurrentDownloads.Value ?? 3), 1, 8));
            await _settings.SetSettingAsync("DownloadDuplicatesPolicy", policy);
            await _settings.SetSettingAsync("SaveMetadataJson", SaveMetadataJson.IsChecked == true);
            await _settings.SetSettingAsync("UseOriginalFilename", UseOriginalFilename.IsChecked == true);
            await _settings.SetSettingAsync("NetworkTimeoutSeconds", Math.Clamp((int)(NetworkTimeoutSeconds.Value ?? 30), 5, 120));
            await _settings.SetSettingAsync("MaxResultsPerSource", Math.Clamp((int)(MaxResultsPerSource.Value ?? 50), 10, 320));
            await _settings.SetSettingAsync("CpuWorkerDegree", Math.Max(1, (int)(CpuWorkerDegree.Value ?? Math.Max(1, Environment.ProcessorCount / 2))));
            await _settings.SetSettingAsync("ThumbnailPrewarmEnabled", ThumbnailPrewarmEnabled.IsChecked == true);
            await _settings.SetSettingAsync("PoolsUpdateIntervalMinutes", Math.Clamp((int)(PoolsUpdateIntervalMinutes.Value ?? 360), 5, 1440));
            // Notify that settings were saved
            try { WeakReferenceMessenger.Default.Send(new SettingsSavedMessage()); } catch { }
        }
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowseDownloadDir(object? sender, RoutedEventArgs e)
    {
        try
        {
            var options = new FolderPickerOpenOptions
            {
                AllowMultiple = false
            };
            var current = DownloadDir.Text;
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                try
                {
                    var suggested = await StorageProvider.TryGetFolderFromPathAsync(current);
                    if (suggested != null)
                    {
                        options.SuggestedStartLocation = suggested;
                    }
                }
                catch { }
            }
            var result = await StorageProvider.OpenFolderPickerAsync(options);
            if (result != null && result.Count > 0)
            {
                var folder = result[0];
                var path = folder.Path?.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    DownloadDir.Text = path;
                }
            }
        }
        catch { }
    }
}
