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
using Avalonia.Platform;
using Avalonia.VisualTree;


namespace Furchive.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _scrollDebounceTimer;
    private ColumnDefinition? _leftCol;
    private ColumnDefinition? _centerCol;
    private ColumnDefinition? _rightCol;
    private RowDefinition? _downloadsRow;

    public MainWindow()
    {
        InitializeComponent();
    // Ensure window icon is set from assets
    try { this.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Furchive/Assets/icon.ico"))); } catch { }
        if (App.Services != null)
        {
            DataContext = App.Services.GetRequiredService<MainViewModel>();
        }

        // Set dynamic title with version: Furchive - VERSION
        try
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "";
            this.Title = string.IsNullOrWhiteSpace(ver) ? "Furchive" : $"Furchive - {ver}";
        }
        catch { }

        // Hook grid columns for persistence
        try
        {
            var grid = this.FindControl<Grid>("MainColumnsGrid");
            if (grid != null && grid.ColumnDefinitions?.Count >= 3)
            {
                _leftCol = grid.ColumnDefinitions[0];
                _centerCol = grid.ColumnDefinitions[1];
                _rightCol = grid.ColumnDefinitions[2];
            }
            RestoreSplitterSizes();
        }
        catch { }

        // Hook downloads row for height persistence
        try
        {
            var root = this.FindControl<Grid>("RootGrid");
            if (root != null && root.RowDefinitions?.Count >= 4)
            {
                _downloadsRow = root.RowDefinitions[3];
            }
            RestoreDownloadsHeight();
        }
        catch { }

    _scrollDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
    _scrollDebounceTimer.Tick += (_, __) => { _scrollDebounceTimer.Stop(); };

        // Keyboard shortcuts: Enter to search, Esc to clear selection (but not while typing in text inputs)
    this.KeyDown += (s, e) =>
        {
            try
            {
                if (DataContext is not MainViewModel vm) return;
                if (e.Key == Key.Enter)
                {
            // Avoid intercepting Enter when a TextBox has focus
            var top = TopLevel.GetTopLevel(this);
            var focused = top?.FocusManager?.GetFocusedElement();
            if (focused is not TextBox)
                    {
                        vm.SearchCommand.Execute(null);
                        e.Handled = true;
                    }
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

        // Auto-load pool when selection changes; auto-scroll pinned list on add
        try
        {
            if (DataContext is MainViewModel vm)
            {
        vm.PropertyChanged += async (_, args) =>
                {
                    // No-op on SelectedPool change; avoid auto-opening a pool when user selects a post.
                };

                // Watch pinned pools collection changes to scroll to bottom
                try
                {
                    vm.PinnedPools.CollectionChanged += (_, e) =>
                    {
                        if (e?.NewItems != null && e.NewItems.Count > 0)
                        {
                            var list = this.FindControl<ListBox>("PinnedPoolsList");
                            if (list?.Items != null)
                            {
                                // Defer until layout updated
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        var last = vm.PinnedPools.LastOrDefault();
                                        if (last != null)
                                        {
                                            list.ScrollIntoView(last);
                                        }
                                    }
                                    catch { }
                                }, DispatcherPriority.Background);
                            }
                        }
                    };
                }
                catch { }

                // Single-click to load a pinned pool
                try
                {
                    var list = this.FindControl<ListBox>("PinnedPoolsList");
                    if (list != null)
                    {
                        list.DoubleTapped -= OnPinnedPoolsDoubleTapped; // disable double-click behavior
                        list.PointerReleased += (s, e) =>
                        {
                            try
                            {
                                if (e.InitialPressMouseButton != MouseButton.Left) return;
                                var item = list.SelectedItem as PoolInfo;
                                if (item == null) return;
                                vm.LoadSelectedPoolCommand.Execute(item);
                                e.Handled = true;
                            }
                            catch { }
                        };
                    }
                }
                catch { }
            }
        }
        catch { }

        // Persist sizes on close
    this.Closing += (_, __) => { try { PersistSplitterSizes(); PersistDownloadsHeight(); } catch { } };
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

    // Preview panel: open actions
    private void OnOpenPreviewInBrowser(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm || vm.SelectedMedia is null) return;
            var url = string.IsNullOrWhiteSpace(vm.SelectedMedia.SourceUrl) ? vm.SelectedMedia.FullImageUrl : vm.SelectedMedia.SourceUrl;
            if (!string.IsNullOrWhiteSpace(url)) App.Services?.GetService<IPlatformShellService>()?.OpenUrl(url);
        }
        catch { }
    }

    private async void OnOpenPreviewInViewer(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm || vm.SelectedMedia is null) return;
            var viewer = new ViewerWindow { DataContext = vm.SelectedMedia };
            await viewer.ShowDialog(this);
        }
        catch { }
    }

    private void OnPreviewImagePressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (sender is Visual vis && e.GetCurrentPoint(vis).Properties.IsLeftButtonPressed)
            {
                OnOpenPreviewInViewer(null, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        catch { }
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

    private void OnPinnedPoolsDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not ListBox lb) return;
            var item = lb.SelectedItem as PoolInfo;
            if (item == null) return;
            vm.LoadSelectedPoolCommand.Execute(item);
            e.Handled = true;
        }
        catch { }
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

    private void OnGalleryScrollChanged(object? sender, ScrollChangedEventArgs e) { /* infinite scroll disabled */ }

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

    // Include/Exclude tag submit helpers
    private void OnAddIncludeTag(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            var tb = this.FindControl<TextBox>("includeTagInput");
            var tag = tb?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                vm.AddIncludeTagCommand.Execute(tag);
                tb!.Text = string.Empty;
            }
        }
        catch { }
    }

    private void OnIncludeTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnAddIncludeTag(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnAddExcludeTag(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            var tb = this.FindControl<TextBox>("excludeTagInput");
            var tag = tb?.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(tag))
            {
                vm.AddExcludeTagCommand.Execute(tag);
                tb!.Text = string.Empty;
            }
        }
        catch { }
    }

    private void OnExcludeTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnAddExcludeTag(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnSaveSearchClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            vm.SaveSearchCommand.Execute(null);
            var tb = this.FindControl<TextBox>("saveSearchInput");
            if (tb != null) tb.Text = string.Empty;
        }
        catch { }
    }

    private void OnSaveSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSaveSearchClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void RestoreSplitterSizes()
    {
        try
        {
            var settings = App.Services?.GetService<ISettingsService>();
            if (settings == null || _leftCol == null || _rightCol == null) return;
            var left = settings.GetSetting<double>("Ui.LeftPaneWidth", 260);
            var right = settings.GetSetting<double>("Ui.RightPaneWidth", 360);
            _leftCol.Width = new GridLength(Math.Max(180, left));
            _rightCol.Width = new GridLength(Math.Max(300, right));
            // Center takes the rest; no need to persist explicitly.
        }
        catch { }
    }

    private void PersistSplitterSizes()
    {
        try
        {
            var settings = App.Services?.GetService<ISettingsService>();
            if (settings == null || _leftCol == null || _rightCol == null) return;
            _ = settings.SetSettingAsync("Ui.LeftPaneWidth", _leftCol.ActualWidth);
            _ = settings.SetSettingAsync("Ui.RightPaneWidth", _rightCol.ActualWidth);
        }
        catch { }
    }

    private void RestoreDownloadsHeight()
    {
        try
        {
            var settings = App.Services?.GetService<ISettingsService>();
            if (settings == null || _downloadsRow == null) return;
            var h = settings.GetSetting<double>("Ui.DownloadsHeight", 220);
            _downloadsRow.Height = new GridLength(Math.Max(160, h));
        }
        catch { }
    }

    private void PersistDownloadsHeight()
    {
        try
        {
            var settings = App.Services?.GetService<ISettingsService>();
            if (settings == null || _downloadsRow == null) return;
            _ = settings.SetSettingAsync("Ui.DownloadsHeight", _downloadsRow.ActualHeight);
        }
        catch { }
    }

    // Splitter drag completed handlers to persist immediately when user stops dragging
    private void OnColumnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        try { PersistSplitterSizes(); } catch { }
    }

    private void OnRowSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        try { PersistDownloadsHeight(); } catch { }
    }
}
