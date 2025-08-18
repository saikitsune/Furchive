using CommunityToolkit.Mvvm.ComponentModel;

namespace Furchive.Avalonia.ViewModels;

// Placeholder VM to get the Avalonia shell running; we'll port the full VM next.
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private string searchText = string.Empty;
}
