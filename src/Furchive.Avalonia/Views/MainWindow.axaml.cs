using Avalonia.Controls;
using Furchive.Avalonia.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using System.Diagnostics;
using System.IO;
using Furchive.Core.Interfaces;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Furchive.Avalonia.Messages;
using Furchive.Avalonia.Infrastructure;
using Avalonia;


namespace Furchive.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _scrollDebounceTimer;

    public MainWindow()
    {
        InitializeComponent();
        if (App.Services != null)
        {
            DataContext = App.Services.GetRequiredService<MainViewModel>();
        }

        _scrollDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _scrollDebounceTimer.Tick += async (_, __) =>
        {
            _scrollDebounceTimer.Stop();
            if (DataContext is MainViewModel vm && vm.HasNextPage && !vm.IsSearching)
            {
                try { await vm.AppendNextPageAsync(); } catch { }
            }
        };

        // Keyboard shortcuts: Enter to search, Esc to clear selection
        this.KeyDown += (s, e) =>
        {
            try
            {
                if (DataContext is not MainViewModel vm) return;
                if (e.Key == Key.Enter)
                {
                    vm.SearchCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    vm.SelectNoneCommand.Execute(null);
                    e.Handled = true;
                }
            }
            catch { }
        };

        // Listen for UI error messages and show a simple dialog
        try
        {
            WeakReferenceMessenger.Default.Register<UiErrorMessage>(this, async (_, msg) =>
            {
                try { await DialogService.ShowInfoAsync(this, msg.Title, msg.Message); } catch { }
            });
        }
        catch { }
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
                try { App.Services?.GetService<IPlatformShellService>()?.OpenPath(path); } catch { }
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
                try { App.Services?.GetService<IPlatformShellService>()?.OpenFolder(dir); } catch { }
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
            App.Services?.GetService<IPlatformShellService>()?.OpenFolder(dir);
        }
        catch { }
    }

    private void OnGalleryScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not ScrollViewer sv) return;
            var extent = sv.Extent.Height;
            var viewport = sv.Viewport.Height;
            var offset = sv.Offset.Y;
            if (extent <= 0 || viewport <= 0) return;
            var remaining = extent - (offset + viewport);
            if (remaining <= viewport * 0.5 && vm.HasNextPage)
            {
                // Debounce to reduce rapid-fire calls; VM also guards concurrency
                _scrollDebounceTimer.Stop();
                _scrollDebounceTimer.Start();
            }
        }
        catch { }
    }

    private void OnGalleryKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not ListBox lb) return;
            var idx = vm.SearchResults.IndexOf(vm.SelectedMedia!);
            int columns = Math.Max(1, (int)Math.Floor((lb.Bounds.Width) / Math.Max(1.0, vm.GalleryTileWidth + 12)));
            int move = 0;
            switch (e.Key)
            {
                case Key.Left: move = -1; break;
                case Key.Right: move = +1; break;
                case Key.Up: move = -columns; break;
                case Key.Down: move = +columns; break;
                case Key.PageUp: move = -columns * 3; break;
                case Key.PageDown: move = +columns * 3; break;
                default: return;
            }
            e.Handled = true;
            if (vm.SearchResults.Count == 0) return;
            if (idx < 0) idx = 0;
            var newIdx = Math.Clamp(idx + move, 0, vm.SearchResults.Count - 1);
            vm.SelectedMedia = vm.SearchResults[newIdx];
            // Scroll into view
            try { lb.ScrollIntoView(vm.SelectedMedia); } catch { }
        }
        catch { }
    }

    // Download queue button handlers
    private async void OnPauseJob(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DownloadJob job)
        {
            try { var svc = App.Services?.GetService<IDownloadService>(); if (svc != null) await svc.PauseDownloadAsync(job.Id); } catch { }
        }
    }
    private async void OnResumeJob(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DownloadJob job)
        {
            try { var svc = App.Services?.GetService<IDownloadService>(); if (svc != null) await svc.ResumeDownloadAsync(job.Id); } catch { }
        }
    }
    private async void OnCancelJob(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DownloadJob job)
        {
            try { var svc = App.Services?.GetService<IDownloadService>(); if (svc != null) await svc.CancelDownloadAsync(job.Id); } catch { }
        }
    }
    private async void OnRetryJob(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DownloadJob job)
        {
            try { var svc = App.Services?.GetService<IDownloadService>(); if (svc != null) await svc.RetryDownloadAsync(job.Id); } catch { }
        }
    }

    private void OnTagChipPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not Control ctrl) return;
            var tag = ctrl.DataContext as string;
            if (string.IsNullOrWhiteSpace(tag)) return;
            var props = e.GetCurrentPoint(ctrl);
            if (props.Properties.IsRightButtonPressed)
            {
                // Right click → exclude
                vm.AddExcludeTagCommand.Execute(tag);
                e.Handled = true;
            }
            else if (props.Properties.IsLeftButtonPressed)
            {
                // Left click → include
                vm.AddIncludeTagCommand.Execute(tag);
                e.Handled = true;
            }
        }
        catch { }
    }
}
