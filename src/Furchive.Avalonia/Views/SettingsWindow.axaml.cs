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
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_settings != null)
        {
            await _settings.SetSettingAsync("DefaultDownloadDirectory", DownloadDir.Text ?? string.Empty);
        }
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
