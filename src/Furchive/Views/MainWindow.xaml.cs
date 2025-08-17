using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Furchive.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Furchive.Core.Interfaces;
using System.Diagnostics;
using Furchive.Core.Models;
using System.Windows.Threading;
using System.IO;
using System.ComponentModel;
using System.Windows.Media.Animation;
using Furchive.Infrastructure;

namespace Furchive.Views;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _downloadsRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private double _desiredPreviewWidth = 333; // remembers last non-zero preview width
    private bool _isResizingPreview;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
    // Load persisted UI sizes first so initial selection uses saved widths
    TryLoadPanelSizes();
    DataContext = viewModel;
    // React to selection changes to animate preview pane
    viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        // Ensure preview starts hidden when nothing selected
        AnimatePreviewPane();
        _downloadsRefreshTimer.Tick += (_, __) =>
        {
            if (DataContext is MainViewModel vm)
            {
                // Lightweight sync since VM already updates on events; this ensures UI refresh if needed
                vm.RefreshDownloadQueueCommand.Execute(null);
            }
        };
        _downloadsRefreshTimer.Start();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedMedia))
        {
            AnimatePreviewPane();
        }
    }

    private void AnimatePreviewPane()
    {
        try
        {
            if (FindName("PreviewColumn") is not ColumnDefinition col) return;
            bool hasSelection = (DataContext as MainViewModel)?.SelectedMedia != null;
            if (_isResizingPreview)
            {
                // If user is actively dragging the splitter, don't fight the user; just ensure it stays visible/hidden
                col.BeginAnimation(ColumnDefinition.WidthProperty, null);
                    col.Width = new GridLength(hasSelection ? Math.Max(250, col.ActualWidth) : 0);
                return;
            }
            // When app starts, ActualWidth may be 0 though we have a saved width; prefer the saved width for target
            double target = hasSelection ? Math.Max(250, _desiredPreviewWidth) : 0;
            double from = col.ActualWidth <= 0 && hasSelection ? target : col.ActualWidth;

            var anim = new GridLengthAnimation
            {
                From = new GridLength(from),
                To = new GridLength(target),
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            col.BeginAnimation(ColumnDefinition.WidthProperty, anim, HandoffBehavior.SnapshotAndReplace);
        }
        catch { }
    }

    private void TryLoadPanelSizes()
    {
        try
        {
            var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
            if (settings == null) return;
            // Downloads panel height
            var downloadsHeight = settings.GetSetting<double>("DownloadsPanelHeight", 200);
            if (downloadsHeight > 50 && downloadsHeight < 1000)
            {
                if (FindName("DownloadsRow") is RowDefinition row)
                {
                    row.Height = new GridLength(downloadsHeight);
                }
            }
            // Columns widths
            var tagWidth = settings.GetSetting<double>("TagPanelWidth", 300);
            if (tagWidth > 150 && tagWidth < 800 && FindName("TagColumn") is ColumnDefinition tagCol)
            {
                tagCol.Width = new GridLength(tagWidth);
            }
            var prevWidth = settings.GetSetting<double>("PreviewPanelWidth", 333);
            if (prevWidth > 200 && prevWidth < 1000 && FindName("PreviewColumn") is ColumnDefinition prevCol)
            {
                prevCol.Width = new GridLength(prevWidth);
                    _desiredPreviewWidth = prevWidth;
            }
        }
        catch { }
    }

    private void SearchQuery_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel viewModel)
        {
            viewModel.SearchCommand.Execute(null);
        }
    }

    private void TagEditor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox textBox && DataContext is MainViewModel viewModel)
        {
            var tag = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(tag))
            {
                // Add to include tags by default, could be enhanced with modifier keys for exclude
                viewModel.AddIncludeTagCommand.Execute(tag);
                textBox.Text = string.Empty;
            }
        }
    }

    private void TagEditorExclude_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is System.Windows.Controls.TextBox textBox && DataContext is MainViewModel viewModel)
        {
            var tag = textBox.Text?.Trim();
            if (!string.IsNullOrEmpty(tag))
            {
                viewModel.AddExcludeTagCommand.Execute(tag);
                textBox.Text = string.Empty;
            }
        }
    }

    private void AddIncludeTag_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            var tb = (this.FindName("includeTagInput") as System.Windows.Controls.TextBox);
            var tag = tb?.Text?.Trim();
            if (!string.IsNullOrEmpty(tag))
            {
                viewModel.AddIncludeTagCommand.Execute(tag);
                if (tb != null) tb.Text = string.Empty;
            }
        }
    }

    private void AddExcludeTag_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            var tb = (this.FindName("excludeTagInput") as System.Windows.Controls.TextBox);
            var tag = tb?.Text?.Trim();
            if (!string.IsNullOrEmpty(tag))
            {
                viewModel.AddExcludeTagCommand.Execute(tag);
                if (tb != null) tb.Text = string.Empty;
            }
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var sp = App.Services;
        if (sp != null)
        {
            var win = sp.GetRequiredService<SettingsWindow>();
            win.Owner = this;
            win.ShowDialog();
            return;
        }
    System.Windows.MessageBox.Show("Unable to open Settings window.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenViewer_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectedMedia == null) return;
        var sp = App.Services;
        if (sp == null) return;
        var viewer = sp.GetRequiredService<ViewerWindow>();
        // Provide next/prev functions using current selection order and API fallback
        int movingIndex = vm.SearchResults.IndexOf(vm.SelectedMedia);
        async Task<(string id, Furchive.Core.Models.MediaItem? item)?> GetNeighbor(int delta)
        {
            movingIndex += delta;
            int idx = movingIndex;
            if (idx >= 0 && idx < vm.SearchResults.Count)
            {
                var neighbor = vm.SearchResults[idx];
                return (neighbor.Id, neighbor);
            }
            // In pool mode, navigation is constrained strictly within the current pool
            if (vm.IsPoolMode)
            {
                return null;
            }
            // If out of current page (non-pool mode), attempt to fetch from API by moving page via VM helper
            var item = await vm.FetchNextFromApiAsync(delta > 0);
            if (item == null) return null;
            // Reset moving index to end or start so subsequent moves continue in the same direction
            movingIndex = delta > 0 ? vm.SearchResults.Count : -1;
            return (item.Id, item);
        }

        viewer.Initialize(vm.SelectedMedia,
            getNext: () => GetNeighbor(+1),
            getPrev: () => GetNeighbor(-1));
        viewer.Owner = this;
        viewer.Show();
    }

    private void GalleryListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ensure the double-click happened on an item and select it if needed
        if (sender is System.Windows.Controls.ListView lv)
        {
            // If no item is selected, try hit-testing
            if (lv.SelectedItem == null)
            {
                var element = e.OriginalSource as DependencyObject;
                while (element != null && element is not System.Windows.Controls.ListViewItem)
                    element = System.Windows.Media.VisualTreeHelper.GetParent(element);
                if (element is System.Windows.Controls.ListViewItem lvi)
                    lvi.IsSelected = true;
            }
        }

        // Reuse existing open-viewer logic
        OpenViewer_Click(sender, new RoutedEventArgs());
    }

    private void GalleryListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // If clicking on blank space within the WrapPanel area, clear selection
        if (sender is System.Windows.Controls.ListView lv)
        {
            var dep = e.OriginalSource as DependencyObject;
            // Walk up to see if we clicked inside a ListViewItem
            while (dep != null && dep is not System.Windows.Controls.ListViewItem)
            {
                dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            }
            if (dep is not System.Windows.Controls.ListViewItem)
            {
                // Blank area: clear selection
                lv.SelectedItem = null;
                e.Handled = false; // allow scrollbars etc.
            }
        }
    }

    private void OpenDownloads_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
    var defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Furchive");
        var path = settings?.GetSetting<string>("DefaultDownloadDirectory", defaultDir) ?? defaultDir;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
    }

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MediaItem item)
        {
            var url = item.SourceUrl;
            if (!string.IsNullOrWhiteSpace(url))
            {
                try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
            }
        }
    }

    private void OpenDownloadedFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadJob job)
        {
            try
            {
                if (File.Exists(job.DestinationPath))
                    Process.Start(new ProcessStartInfo { FileName = job.DestinationPath, UseShellExecute = true });
            }
            catch { }
        }
    }

    private void OpenDownloadedFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadJob job)
        {
            try
            {
                var path = job.DestinationPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    // If the path itself is a directory (e.g., aggregate group), open it directly
                    if (Directory.Exists(path))
                    {
                        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    }
                    else
                    {
                        // Otherwise open the folder containing the file
                        var dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                    }
                }
            }
            catch { }
        }
    }

    private void PoolsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.AddedItems != null && e.AddedItems.Count > 0)
        {
            vm.LoadSelectedPoolCommand.Execute(null);
        }
    }

    private void PoolsList_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.LoadSelectedPoolCommand.Execute(null);
        }
    }

    private void PoolsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            vm.LoadSelectedPoolCommand.Execute(null);
        }
    }

    private bool _downloadsSortDesc = true;
    private string? _downloadsSortPath;
    private void DownloadGridHeader_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (sender is not GridViewColumnHeader header) return;
        var path = header.Tag as string;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (_downloadsSortPath == path) _downloadsSortDesc = !_downloadsSortDesc;
        else { _downloadsSortPath = path; _downloadsSortDesc = true; }

        var cv = System.Windows.Data.CollectionViewSource.GetDefaultView(vm.DownloadQueue) as System.Windows.Data.ListCollectionView;
        if (cv == null) return;
        cv.SortDescriptions.Clear();
        // Map known paths to property names
        string prop = path;
        if (path == "MediaItem.Title") prop = "MediaItem.Title";
        else if (path == "Status") prop = "Status";
        else if (path == "ProgressPercent") prop = "ProgressPercent";
        else if (path == "TotalBytes") prop = "TotalBytes";
        cv.SortDescriptions.Add(new SortDescription(prop, _downloadsSortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending));
        cv.Refresh();
    }

    // Save downloads panel height on window size changes end
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        SaveDownloadsPanelHeight();
    }

    private void SaveDownloadsPanelHeight()
    {
        try
        {
            if (FindName("DownloadsRow") is RowDefinition row)
            {
                var height = row.ActualHeight;
                if (height > 0)
                {
                    var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
                    if (settings != null)
                    {
                        _ = settings.SetSettingAsync("DownloadsPanelHeight", height);
                    }
                }
            }
        }
        catch { }
    }

    private void ColumnSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        try
        {
            var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
            if (settings == null) return;
            if (FindName("TagColumn") is ColumnDefinition tag)
            {
                _ = settings.SetSettingAsync("TagPanelWidth", tag.ActualWidth);
            }
            if (FindName("PreviewColumn") is ColumnDefinition prev)
            {
                _desiredPreviewWidth = Math.Max(250, prev.ActualWidth);
                _ = settings.SetSettingAsync("PreviewPanelWidth", _desiredPreviewWidth);
            }
        }
        catch { }
        finally { _isResizingPreview = false; }
    }

    private void RowSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SaveDownloadsPanelHeight();
    }

    private void ColumnSplitter_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _isResizingPreview = true;
        try
        {
            if (FindName("PreviewColumn") is ColumnDefinition prev)
            {
                // Stop any running animation and take control for live-resize
                prev.BeginAnimation(ColumnDefinition.WidthProperty, null);
                _desiredPreviewWidth = Math.Max(250, prev.ActualWidth);
            }
        }
        catch { }
    }
}
