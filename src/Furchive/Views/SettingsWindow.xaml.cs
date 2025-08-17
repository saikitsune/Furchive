using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Furchive.ViewModels;
using ModernWpf;
using Furchive.Core.Interfaces;
using System.Diagnostics;
using WinForms = System.Windows.Forms;

namespace Furchive.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    // Removed FA/InkBunny password handlers; focusing on e621 only

    private void Theme_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            var idx = (sender as System.Windows.Controls.ComboBox)?.SelectedIndex ?? 0;
            var mode = idx == 1 ? "light" : idx == 2 ? "dark" : "system";
            // Persist via settings service will be done in Save; apply immediately for preview
            if (string.Equals(mode, "system", StringComparison.OrdinalIgnoreCase))
                ThemeManager.Current.ApplicationTheme = null;
            else if (mode == "light")
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
            else
                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;

            // Save theme mode immediately for responsiveness
            var settings = App.Services?.GetService(typeof(ISettingsService)) as ISettingsService;
            if (settings != null)
            {
                _ = settings.SetSettingAsync("ThemeMode", mode);
            }
        }
    }

    // Removed FA/InkBunny/Weasyl test handlers; focusing on e621 only

    private async void TestE621_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
    if (!vm.IsE621Valid(out var msg)) { System.Windows.MessageBox.Show(msg, "e621", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var apis = App.Services?.GetService(typeof(IEnumerable<IPlatformApi>)) as IEnumerable<IPlatformApi>;
    var e621 = apis?.FirstOrDefault(p => p.PlatformName == "e621");
    if (e621 == null) { System.Windows.MessageBox.Show("e621 API not available.", "e621", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        var creds = new Dictionary<string, string>
        {
            ["UserAgent"] = vm.E621UserAgent ?? string.Empty
        };
    if (!string.IsNullOrWhiteSpace(vm.E621Username)) creds["Username"] = vm.E621Username!.Trim();
    if (!string.IsNullOrWhiteSpace(vm.E621ApiKey)) creds["ApiKey"] = vm.E621ApiKey!.Trim();

        await RunAuthHealthAsync(e621, creds, "e621");
    }

    private async Task RunAuthHealthAsync(IPlatformApi api, Dictionary<string, string> creds, string name)
    {
        try
        {
            var ok = await api.AuthenticateAsync(creds);
            var health = await api.GetHealthAsync();
            if (!health.IsAvailable)
            {
                System.Windows.MessageBox.Show($"{name} is not reachable right now.", name, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var authState = health.IsAuthenticated ? "authenticated" : (ok ? "credentials set" : "unauthenticated");
            var icon = health.IsAuthenticated ? MessageBoxImage.Information : MessageBoxImage.Warning;
            System.Windows.MessageBox.Show($"{name} {authState}. Rate limit remaining: {health.RateLimitRemaining}", name, MessageBoxButton.OK, icon);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"{name} test failed: {ex.Message}", name, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var path = vm.CachePath;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); } catch { }
    }

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try { Process.Start(new ProcessStartInfo { FileName = vm.DefaultDownloadDirectory, UseShellExecute = true }); } catch { }
    }

    private void OpenTempFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        try { Process.Start(new ProcessStartInfo { FileName = vm.TempPath, UseShellExecute = true }); } catch { }
    }

    private void BrowseDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
    using var dlg = new WinForms.FolderBrowserDialog();
        dlg.Description = "Choose default download folder";
        dlg.SelectedPath = vm.DefaultDownloadDirectory;
    var result = dlg.ShowDialog();
    if (result == WinForms.DialogResult.OK)
        {
            vm.DefaultDownloadDirectory = dlg.SelectedPath;
        }
    }
}
