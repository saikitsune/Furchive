using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia;

namespace Furchive.Avalonia.Infrastructure;

public static class DialogService
{
    public static async Task ShowInfoAsync(Window owner, string title, string message)
    {
        var tb = new TextBlock { Text = message, Margin = new Thickness(16) };
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 220,
            Content = tb
        };
        await dialog.ShowDialog(owner);
    }
}
