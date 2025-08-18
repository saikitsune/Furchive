using Avalonia.Controls;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using System.Diagnostics;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    public ViewerWindow()
    {
        InitializeComponent();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenOriginal(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.FullImageUrl))
        {
            try { Process.Start(new ProcessStartInfo { FileName = m.FullImageUrl, UseShellExecute = true }); } catch { }
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.SourceUrl))
        {
            try { Process.Start(new ProcessStartInfo { FileName = m.SourceUrl, UseShellExecute = true }); } catch { }
        }
    }
}
