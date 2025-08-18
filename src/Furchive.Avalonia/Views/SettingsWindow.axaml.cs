using Avalonia.Controls;
using Avalonia.Interactivity;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

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
    FilenameTemplate.Text = _settings?.GetSetting<string>("FilenameTemplate", "{source}/{artist}/{id}.{ext}") ?? "{source}/{artist}/{id}.{ext}";
    PoolFilenameTemplate.Text = _settings?.GetSetting<string>("PoolFilenameTemplate", "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}") ?? "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}";
    try { PrefetchPagesAhead.Value = _settings?.GetSetting<int>("E621SearchPrefetchPagesAhead", 2) ?? 2; } catch { PrefetchPagesAhead.Value = 2; }
    try { PrefetchParallelism.Value = _settings?.GetSetting<int>("E621SearchPrefetchParallelism", 2) ?? 2; } catch { PrefetchParallelism.Value = 2; }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DownloadDir.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621Username", E621User.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621ApiKey", E621Key.Text ?? string.Empty);
            await _settings.SetSettingAsync("FilenameTemplate", FilenameTemplate.Text ?? string.Empty);
            await _settings.SetSettingAsync("PoolFilenameTemplate", PoolFilenameTemplate.Text ?? string.Empty);
            await _settings.SetSettingAsync("E621SearchPrefetchPagesAhead", (int)(PrefetchPagesAhead.Value ?? 2));
            await _settings.SetSettingAsync("E621SearchPrefetchParallelism", (int)(PrefetchParallelism.Value ?? 2));
        }
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
