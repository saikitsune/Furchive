using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Furchive.Avalonia.ViewModels;
using Furchive.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Furchive.Avalonia.Messages;
using Furchive.Core.Models;

namespace Furchive.Avalonia.Views;

public partial class MainWindow : Window
{
    private ColumnDefinition? _leftCol;
    private ColumnDefinition? _rightCol;
    private RowDefinition? _downloadsRow;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            var services = App.Services ?? throw new InvalidOperationException("Service provider not initialized");
            DataContext = services.GetRequiredService<MainViewModel>();
            // Subscribe to viewer open messages (MVVM: window creation in view layer)
            try
            {
                WeakReferenceMessenger.Default.Register<OpenViewerMessage>(this, (_, msg) =>
                {
                    try
                    {
                        if (msg.Value == null) return;
                        var vw = new ViewerWindow { DataContext = msg.Value };
                        vw.Show(this);
                    }
                    catch { }
                });
            }
            catch { }
            try { Icon = new WindowIcon(global::Avalonia.Platform.AssetLoader.Open(new Uri("avares://Furchive/Assets/icon.ico"))); } catch { }
            try
            {
                var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? string.Empty;
                Title = string.IsNullOrWhiteSpace(ver) ? "Furchive" : $"Furchive - {ver}";
            }
            catch { }
            try
            {
                var grid = this.FindControl<Grid>("MainColumnsGrid");
                if (grid?.ColumnDefinitions?.Count >= 3)
                {
                    _leftCol = grid.ColumnDefinitions[0];
                    _rightCol = grid.ColumnDefinitions[2];
                }
                var root = this.FindControl<Grid>("RootGrid");
                if (root?.RowDefinitions?.Count >= 4)
                {
                    _downloadsRow = root.RowDefinitions[3];
                }
                RestoreSplitterSizes();
                RestoreDownloadsHeight();
            }
            catch { }
            Closing += (_, __) => { try { PersistSplitterSizes(); PersistDownloadsHeight(); } catch { } };
        }
        catch (Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Furchive", "logs", "debug.log");
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, $"[MainWindow Exception] {DateTime.Now}: {ex}\n");
            }
            catch { }
            // Propagate so App startup can catch and display a fallback error window instead of silently exiting
            throw new InvalidOperationException("Failed to construct MainWindow", ex);
        }
    }

    private async void OnPoolsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.OnPoolSelectionChangedAsync();
            }
        }
        catch { }
    }

    private void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            var wnd = new SettingsWindow();
            wnd.Show(this);
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


    private void OnIncludeTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel vm)
            {
                var tb = this.FindControl<TextBox>("includeTagInput");
                var tag = tb?.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    vm.AddIncludeTagCommand.Execute(tag);
                    if (tb != null) tb.Text = string.Empty;
                }
            }
            e.Handled = true;
        }
    }
    private void OnExcludeTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel vm)
            {
                var tb = this.FindControl<TextBox>("excludeTagInput");
                var tag = tb?.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    vm.AddExcludeTagCommand.Execute(tag);
                    if (tb != null) tb.Text = string.Empty;
                }
            }
            e.Handled = true;
        }
    }
    private void OnSaveSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SaveSearchCommand.Execute(null);
                var tb = this.FindControl<TextBox>("saveSearchInput");
                if (tb != null) tb.Text = string.Empty;
            }
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

    private DateTime _lastClickTime = DateTime.MinValue;
    private void OnGalleryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            if (e.ClickCount >= 2)
            {
                if (DataContext is MainViewModel vm && vm.SelectedMedia != null)
                {
                    WeakReferenceMessenger.Default.Send(new OpenViewerMessage(vm.SelectedMedia));
                    e.Handled = true;
                }
                return;
            }
            // Fallback manual timing (in case ClickCount not reliable on platform)
            var now = DateTime.UtcNow;
            if ((now - _lastClickTime).TotalMilliseconds < 400)
            {
                if (DataContext is MainViewModel vm2 && vm2.SelectedMedia != null)
                {
                    WeakReferenceMessenger.Default.Send(new OpenViewerMessage(vm2.SelectedMedia));
                    e.Handled = true;
                }
            }
            _lastClickTime = now;
        }
        catch { }
    }

    private void OnPreviewImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vm && vm.SelectedMedia != null)
            {
                WeakReferenceMessenger.Default.Send(new OpenViewerMessage(vm.SelectedMedia));
            }
        }
        catch { }
    }

    private async void OnPinnedPoolsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is ListBox lb && lb.SelectedItem is PoolInfo pool)
            {
                await vm.LoadPinnedPoolAsync(pool);
                // Optional: keep selection highlighted; comment out next line if not desired.
                // lb.SelectedItem = null;
            }
        }
        catch { }
    }
}
