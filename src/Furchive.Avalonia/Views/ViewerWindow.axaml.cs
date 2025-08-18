using Avalonia.Controls;
using Avalonia.Interactivity;
using Furchive.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Furchive.Core.Interfaces;
using System;
using Avalonia.Input;

namespace Furchive.Avalonia.Views;

public partial class ViewerWindow : Window
{
    public ViewerWindow()
    {
        InitializeComponent();
        this.KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
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
