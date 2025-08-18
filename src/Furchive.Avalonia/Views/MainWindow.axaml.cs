using Avalonia.Controls;
using Furchive.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Interactivity;

namespace Furchive.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (App.Services != null)
        {
            DataContext = App.Services.GetRequiredService<MainViewModel>();
        }
    }

    private async void OnOpenDownloads(object? sender, RoutedEventArgs e)
    {
        var dlg = new Window { Width = 800, Height = 600, Title = "Downloads (placeholder)" };
        await dlg.ShowDialog(this);
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
    var dlg = new SettingsWindow();
    await dlg.ShowDialog(this);
    }
}
