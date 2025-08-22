using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Animation;
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
    private ColumnDefinition? _topLeftCol;
    private ColumnDefinition? _topRightCol;

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
                        // Build navigation context from current gallery so Prev/Next work (esp. pools)
                        if (DataContext is MainViewModel vm)
                        {
                            var list = vm.SearchResults.ToList();
                            var idx = list.FindIndex(m => m.Id == msg.Value.Id);
                            if (idx < 0) idx = 0;
                            int? poolId = (vm.IsPoolMode && vm.CurrentPoolId.HasValue) ? vm.CurrentPoolId : null;
                            var vw = new ViewerWindow();
                            vw.InitializeNavigationContext(list, idx, poolId);
                            vw.Show(this);
                        }
                    }
                    catch { }
                });
                WeakReferenceMessenger.Default.Register<OpenViewerRequestMessage>(this, (_, msg) =>
                {
                    try
                    {
                        var req = msg.Value;
                        if (req == null || req.Items.Count == 0) return;
                        var idx = Math.Clamp(req.Index, 0, req.Items.Count - 1);
                        var vw = new ViewerWindow();
                        vw.InitializeNavigationContext(req.Items, idx, req.PoolId);
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
                var top = this.FindControl<Grid>("TopBarGrid");
                if (top?.ColumnDefinitions?.Count >= 3)
                {
                    _topLeftCol = top.ColumnDefinitions[0];
                    _topRightCol = top.ColumnDefinitions[2];
                }
                RestoreSplitterSizes();
                SyncTopBarWidths();
            }
            catch { }
            Closing += (_, __) => { try { PersistSplitterSizes(); } catch { } };
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

    // Downloads panel no longer has its own dedicated row spanning full width; height persistence removed.

    // Splitter drag completed handlers to persist immediately when user stops dragging
    private void OnColumnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        try { PersistSplitterSizes(); } catch { }
        try { SyncTopBarWidths(); } catch { }
    }

    private void OnRowSplitterDragCompleted(object? sender, VectorEventArgs e) { }

    private void OnAddIncludeTagClick(object? sender, RoutedEventArgs e)
    {
        try
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
        }
        catch { }
    }

    private void OnAddExcludeTagClick(object? sender, RoutedEventArgs e)
    {
        try
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
        }
        catch { }
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
                    var list = vm.SearchResults.ToList();
                    var idx = list.FindIndex(m => m.Id == vm.SelectedMedia.Id);
                    if (idx < 0) idx = 0;
                    int? poolId = (vm.IsPoolMode && vm.CurrentPoolId.HasValue) ? vm.CurrentPoolId : null;
                    WeakReferenceMessenger.Default.Send(new OpenViewerRequestMessage(new OpenViewerRequest(list, idx, poolId)));
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
                    var list = vm2.SearchResults.ToList();
                    var idx = list.FindIndex(m => m.Id == vm2.SelectedMedia.Id);
                    if (idx < 0) idx = 0;
                    int? poolId = (vm2.IsPoolMode && vm2.CurrentPoolId.HasValue) ? vm2.CurrentPoolId : null;
                    WeakReferenceMessenger.Default.Send(new OpenViewerRequestMessage(new OpenViewerRequest(list, idx, poolId)));
                    e.Handled = true;
                }
            }
        }
        catch { }
    }

    private void OnPreviewImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vm && vm.SelectedMedia != null)
            {
                var list = vm.SearchResults.ToList();
                var idx = list.FindIndex(m => m.Id == vm.SelectedMedia.Id);
                if (idx < 0) idx = 0;
                int? poolId = (vm.IsPoolMode && vm.CurrentPoolId.HasValue) ? vm.CurrentPoolId : null;
                WeakReferenceMessenger.Default.Send(new OpenViewerRequestMessage(new OpenViewerRequest(list, idx, poolId)));
            }
        }
        catch { }
    }

    private void SyncTopBarWidths()
    {
        try
        {
            if (_leftCol == null || _rightCol == null || _topLeftCol == null || _topRightCol == null) return;
            _topLeftCol.Width = new GridLength(_leftCol.ActualWidth);
            _topRightCol.Width = new GridLength(_rightCol.ActualWidth);
        }
        catch { }
    }

    private void OnGalleryDoubleTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (DataContext is MainViewModel vm && vm.SelectedMedia != null)
            {
                var list = vm.SearchResults.ToList();
                var idx = list.FindIndex(m => m.Id == vm.SelectedMedia.Id);
                if (idx < 0) idx = 0;
                int? poolId = (vm.IsPoolMode && vm.CurrentPoolId.HasValue) ? vm.CurrentPoolId : null;
                WeakReferenceMessenger.Default.Send(new OpenViewerRequestMessage(new OpenViewerRequest(list, idx, poolId)));
            }
        }
        catch { }
    }

    private bool _downloadsExpanded = false;
    private double _downloadsExpandedHeight = 220; // default full height (will be overridden by saved setting)
    private bool _isResizingDownloads = false;
    private global::Avalonia.Point _resizeStartPoint;
    private double _initialDownloadsHeight;
    private void OnToggleDownloadsPanelClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var body = this.FindControl<Border>("DownloadsBody");
            var toggle = this.FindControl<Button>("DownloadsToggleButton");
            if (body == null || toggle == null) return;

            if (!_downloadsExpanded)
            {
                // Expand
                // Load persisted height if available
                try
                {
                    var settings = App.Services?.GetService<ISettingsService>();
                    if (settings != null)
                    {
                        var saved = settings.GetSetting<double>("Ui.DownloadsPanelHeight", _downloadsExpandedHeight);
                        if (saved > 40 && saved < 1200) _downloadsExpandedHeight = saved;
                    }
                }
                catch { }
                body.Height = _downloadsExpandedHeight;
                toggle.Content = "v"; // arrow down to collapse
                _downloadsExpanded = true;
            }
            else
            {
                // Collapse (remember height for future expands)
                _downloadsExpandedHeight = body.Height <= 0 ? _downloadsExpandedHeight : body.Height;
                body.Height = 0;
                toggle.Content = "^";
                _downloadsExpanded = false;
            }
        }
        catch { }
    }

    private void OnDownloadsResizeHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var body = this.FindControl<Border>("DownloadsBody");
            if (body == null) return;
            _isResizingDownloads = true;
            _resizeStartPoint = e.GetPosition(this);
            // If currently collapsed start from zero but use stored expanded height baseline
            if (body.Height <= 0.1)
            {
                // Start resize only after expand toggle; ignore press while collapsed
                _isResizingDownloads = false;
                return;
            }
            _initialDownloadsHeight = body.Height;
            e.Pointer.Capture(sender as IInputElement);
            e.Handled = true;
        }
        catch { }
    }
    private void OnDownloadsResizeHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!_isResizingDownloads) return;
        try
        {
            var body = this.FindControl<Border>("DownloadsBody");
            if (body == null) return;
            var current = e.GetPosition(this);
            var dy = current.Y - _resizeStartPoint.Y;
            // Reverse direction so dragging up increases height (panel grows upward)
            var newHeight = Math.Clamp(_initialDownloadsHeight - dy, 60, 900);
            body.Height = newHeight;
            _downloadsExpandedHeight = newHeight;
            e.Handled = true;
        }
        catch { }
    }
    private void OnDownloadsResizeHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isResizingDownloads) return;
        try
        {
            _isResizingDownloads = false;
            // Pointer capture release not strictly necessary; Avalonia handles implicit capture end.
            // Persist new height
            try
            {
                var settings = App.Services?.GetService<ISettingsService>();
                if (settings != null)
                {
                    _ = settings.SetSettingAsync("Ui.DownloadsPanelHeight", _downloadsExpandedHeight);
                }
            }
            catch { }
            e.Handled = true;
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

    private void OnTagChipPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not Border border) return;
            if (border.DataContext is not string tag || string.IsNullOrWhiteSpace(tag)) return;
            // Left = include, Right = exclude
            var button = e.InitialPressMouseButton;
            if (button == MouseButton.Right)
            {
                if (!vm.ExcludeTags.Contains(tag)) vm.AddExcludeTagCommand.Execute(tag);
            }
            else if (button == MouseButton.Left)
            {
                if (!vm.IncludeTags.Contains(tag)) vm.AddIncludeTagCommand.Execute(tag);
            }
        }
        catch { }
    }
}
