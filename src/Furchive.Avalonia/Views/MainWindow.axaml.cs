using Avalonia.Controls;
using Furchive.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using System.Diagnostics;
using System.IO;
using Furchive.Core.Interfaces;
using Avalonia.Input;

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

    private async void OnGalleryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedMedia != null)
        {
            var viewer = new ViewerWindow();
            // Pass selected media to the viewer for now via DataContext
            viewer.DataContext = vm.SelectedMedia;
            await viewer.ShowDialog(this);
        }
    }

    private void OnOpenDownloaded(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DownloadJob job)
        {
            var path = job.DestinationPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
            }
        }
    }

    private void OnOpenDownloadedFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DownloadJob job)
        {
            var path = job.DestinationPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var dir = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
                try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); } catch { }
            }
        }
    }

    private void OnOpenDownloadsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var settings = App.Services?.GetService<ISettingsService>();
            var dir = settings?.GetSetting<string>("DefaultDownloadDirectory", "");
            if (string.IsNullOrWhiteSpace(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }
}
