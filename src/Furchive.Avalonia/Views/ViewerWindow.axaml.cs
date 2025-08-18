using Avalonia.Controls;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Furchive.Core.Interfaces;

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
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.FullImageUrl); } catch { }
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MediaItem m && !string.IsNullOrWhiteSpace(m.SourceUrl))
        {
            try { App.Services?.GetService<IPlatformShellService>()?.OpenUrl(m.SourceUrl); } catch { }
        }
    }
}
