using System;
using System.IO;
using System.Threading.Tasks;
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
            // Defer auto-expand of downloads until after initial restored queue population finishes.
            _autoExpandDownloadsArmed = false;
            // Initialize downloads panel collapsed by default (will auto-expand on first job or user toggle)
            try
            {
                var body = this.FindControl<Border>("DownloadsBody");
                var toggle = this.FindControl<Button>("DownloadsToggleButton");
                if (body != null && toggle != null)
                {
                    // Load previously saved expanded height (don't expand yet)
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
                    body.Height = 0; // collapsed
                    toggle.Content = "^"; // up arrow indicates can expand
                    _downloadsExpanded = false;
                }
            }
            catch { }
            // Auto-expand on first job arrival if user has collapsed previously
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.DownloadQueue.CollectionChanged += (s, e) =>
                    {
                        try
                        {
                            if (!_autoExpandDownloadsArmed) return; // ignore initial restoration
                            if (vm.DownloadQueue.Count > 0)
                            {
                                var body2 = this.FindControl<Border>("DownloadsBody");
                                var toggle2 = this.FindControl<Button>("DownloadsToggleButton");
                                if (body2 != null && toggle2 != null && body2.Height <= 0)
                                {
                                    body2.Height = Math.Max(100, _downloadsExpandedHeight > 0 ? _downloadsExpandedHeight : 220);
                                    toggle2.Content = "v";
                                    _downloadsExpanded = true;
                                }
                            }
                        }
                        catch { }
                    };
                }
            }
            catch { }
            // Arm auto-expand shortly AFTER window opens so restored jobs don't trigger expansion.
            Opened += async (_, __) =>
            {
                try
                {
                    await Task.Delay(400); // allow initial queue restoration events
                    if (!_userManuallyExpandedDownloads)
                    {
                        var body = this.FindControl<Border>("DownloadsBody");
                        var toggle = this.FindControl<Button>("DownloadsToggleButton");
                        if (body != null && toggle != null && body.Height > 0 && _downloadsExpanded)
                        {
                            // Force collapse if it got expanded by early logic
                            _downloadsExpandedHeight = body.Height;
                            body.Height = 0;
                            toggle.Content = "^";
                            _downloadsExpanded = false;
                        }
                    }
                    _autoExpandDownloadsArmed = true; // future new jobs can auto-expand
                }
                catch { }
            };
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
                // Hook downloads datagrid loaded to finalize min widths based on rendered content
                try
                {
                    var dg = this.FindControl<DataGrid>("DownloadsDataGrid");
                    if (dg != null)
                    {
                        dg.Loaded += (_, __) =>
                        {
                            try { LockDownloadColumnMinimums(dg); } catch { }
                        };
                    }
                }
                catch { }
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
            // Global page navigation shortcuts (Left/Right) when window focused and not typing in a text box
            try { KeyDown += OnWindowKeyDown; } catch { }
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

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            // Only interested in Left / Right arrows
            if (e.Key != Key.Left && e.Key != Key.Right) return;
            // Ignore if a TextBox currently has focus (user typing)
            try
            {
                var focused = this.FocusManager?.GetFocusedElement();
                if (focused is TextBox) return;
            }
            catch { }
            if (DataContext is not MainViewModel vm) return;
            if (e.Key == Key.Right)
            {
                if (vm.CanGoNext && vm.NextPageCommand.CanExecute(null))
                {
                    vm.NextPageCommand.Execute(null);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Left)
            {
                if (vm.CanGoPrev && vm.PrevPageCommand.CanExecute(null))
                {
                    vm.PrevPageCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
        catch { }
    }
    private void LockDownloadColumnMinimums(DataGrid dg)
    {
        try
        {
            // Attempt to restore saved widths first (only on initial lock)
            try
            {
                var settings = App.Services?.GetService<ISettingsService>();
                if (settings != null)
                {
                    var raw = settings.GetSetting<string>("Ui.DownloadsColWidths", string.Empty);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length == dg.Columns.Count)
                        {
                            for (int i = 0; i < parts.Length; i++)
                            {
                                if (double.TryParse(parts[i], out var w) && w > 24)
                                {
                                    try { dg.Columns[i].Width = new DataGridLength(w, DataGridLengthUnitType.Pixel); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // After layout, use actual width as minimum to avoid shrinking below baseline and cache widths for future changes.
            _lastDownloadColWidths = new double[dg.Columns.Count];
            for (int i = 0; i < dg.Columns.Count; i++)
            {
                var col = dg.Columns[i];
                var actual = col.ActualWidth;
                if (actual > 10 && col.MinWidth < actual)
                {
                    col.MinWidth = actual; // Lock initial min to fully visible content
                }
                _lastDownloadColWidths[i] = actual;
            }

            // Subscribe to layout updates to detect user resize and persist (debounced)
            dg.LayoutUpdated += (_, __) =>
            {
                try { MaybePersistDownloadColumnWidths(dg); } catch { }
            };
        }
        catch { }
    }

    private double[]? _lastDownloadColWidths;
    private DateTime _lastDownloadColPersistRequest = DateTime.MinValue;
    private bool _pendingPersist;
    private async void MaybePersistDownloadColumnWidths(DataGrid dg)
    {
        if (dg.Columns.Count == 0) return;
        if (_lastDownloadColWidths == null || _lastDownloadColWidths.Length != dg.Columns.Count)
        {
            _lastDownloadColWidths = new double[dg.Columns.Count];
            for (int i = 0; i < dg.Columns.Count; i++) _lastDownloadColWidths[i] = dg.Columns[i].ActualWidth;
            return;
        }
        bool changed = false;
        for (int i = 0; i < dg.Columns.Count; i++)
        {
            var w = dg.Columns[i].ActualWidth;
            if (Math.Abs(w - _lastDownloadColWidths[i]) > 0.9) { changed = true; _lastDownloadColWidths[i] = w; }
        }
        if (!changed) return;
        _lastDownloadColPersistRequest = DateTime.UtcNow;
        if (_pendingPersist) return; // debounce in flight
        _pendingPersist = true;
        // debounce 600ms
        await Task.Delay(600);
        try
        {
            // If another resize happened recently keep waiting until quiet period
            if ((DateTime.UtcNow - _lastDownloadColPersistRequest).TotalMilliseconds < 550)
            {
                _pendingPersist = false;
                MaybePersistDownloadColumnWidths(dg); // re-evaluate
                return;
            }
            var settings = App.Services?.GetService<ISettingsService>();
            if (settings != null && _lastDownloadColWidths != null)
            {
                var serialized = string.Join(',', _lastDownloadColWidths.Select(v => Math.Round(v, 0).ToString()));
                _ = settings.SetSettingAsync("Ui.DownloadsColWidths", serialized);
            }
        }
        catch { }
        finally { _pendingPersist = false; }
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
            // Primary detection using platform click count from pointer event
            var pt = e.GetCurrentPoint(null);
            // Avalonia PointerPoint lacks ClickCount; retain earlier event argument ClickCount logic via PointerPressed args (already removed DoubleTapped). Use timing fallback below.
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
                _lastClickTime = DateTime.UtcNow; // reset double-click timing baseline
                return;
            }
            // Fallback manual timing (in case ClickCount unreliable on some platforms)
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
                _lastClickTime = now; // prevent triple-trigger
            }
            else
            {
                _lastClickTime = now;
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

    private async void OnPoolNameButtonClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not MainViewModel vm) return;
            if (sender is not Button btn) return;
            var poolName = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(poolName)) return;
            // Attempt to derive pool id from selected media tag categories first
            int poolId = 0;
            try
            {
                var media = vm.SelectedMedia;
                if (media?.TagCategories != null && media.TagCategories.TryGetValue("pool_id", out var idList) && idList.Count > 0)
                {
                    int.TryParse(idList[0], out poolId);
                }
            }
            catch { }
            if (poolId <= 0)
            {
                // Fallback: search in pools list
                var match = vm.Pools.FirstOrDefault(p => string.Equals(p.Name, poolName, StringComparison.OrdinalIgnoreCase));
                if (match != null) poolId = match.Id;
            }
            if (poolId > 0)
            {
                await vm.LoadPoolByIdAsync(poolId, poolName);
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


    private bool _downloadsExpanded = false;
    private double _downloadsExpandedHeight = 220; // default full height (will be overridden by saved setting)
    private bool _autoExpandDownloadsArmed = false; // armed only after startup restoration
    private bool _userManuallyExpandedDownloads = false; // track manual user expansion to avoid forced collapse
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
                _userManuallyExpandedDownloads = true; // mark manual action
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
            // Special handling: clicking 'pool_name:<name>' or 'pool_id:<id>' loads that pool instead of tag include/exclude.
            // We expect pool_name/page_number/pool_id tags stored in TagCategories with raw value (not prefixed). Values are the chip content.
            // Recognize either exact 'pool_name'/'pool_id' category association by inspecting parent ItemsControl DataContext if possible.
            try
            {
                // Infer category key by walking visual tree (border -> parent ItemsControl whose DataContext is KeyValuePair<string,List<string>>)
                var parent = border.Parent;
                while (parent != null && parent is not ItemsControl) parent = (parent as Control)?.Parent;
                string? categoryKey = null;
                if (parent is ItemsControl ic && ic.DataContext is KeyValuePair<string, List<string>> kvp)
                {
                    categoryKey = kvp.Key;
                }
                if (!string.IsNullOrWhiteSpace(categoryKey) && (string.Equals(categoryKey, "pool_name", StringComparison.OrdinalIgnoreCase) || string.Equals(categoryKey, "pool_id", StringComparison.OrdinalIgnoreCase) || string.Equals(categoryKey, "page_number", StringComparison.OrdinalIgnoreCase)))
                {
                    // Attempt to parse pool id either from category data (pool_id tag) or via lookup by name
                    int poolId = 0;
                    string? poolName = null;
                    if (string.Equals(categoryKey, "pool_id", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(tag, out poolId);
                    }
                    else if (string.Equals(categoryKey, "pool_name", StringComparison.OrdinalIgnoreCase))
                    {
                        poolName = tag;
                        // If currently selected media has pool_id we can use that for faster direct load
                        try
                        {
                            var media = vm.SelectedMedia;
                            if (media?.TagCategories != null && media.TagCategories.TryGetValue("pool_id", out var idList) && idList.Count > 0)
                            {
                                int.TryParse(idList[0], out poolId);
                            }
                        }
                        catch { }
                    }
                    else if (string.Equals(categoryKey, "page_number", StringComparison.OrdinalIgnoreCase))
                    {
                        // We are on a page number chip; derive pool id/name from other tag categories if present
                        try
                        {
                            var media = vm.SelectedMedia;
                            if (media?.TagCategories != null)
                            {
                                if (media.TagCategories.TryGetValue("pool_id", out var idList) && idList.Count > 0)
                                {
                                    int.TryParse(idList[0], out poolId);
                                }
                                if (media.TagCategories.TryGetValue("pool_name", out var nameList) && nameList.Count > 0)
                                {
                                    poolName = nameList[0];
                                }
                            }
                        }
                        catch { }
                    }
                    if (poolId <= 0)
                    {
                        // Fallback: attempt to resolve name to existing pool list entry
                        var match = vm.Pools.FirstOrDefault(p => string.Equals(p.Name, poolName, StringComparison.OrdinalIgnoreCase));
                        if (match != null) poolId = match.Id;
                    }
                    if (poolId > 0)
                    {
                        _ = vm.LoadPoolByIdAsync(poolId, poolName);
                        e.Handled = true;
                        return;
                    }
                }
            }
            catch { }

            // Default: Left = include, Right = exclude
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
