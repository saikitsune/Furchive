namespace Furchive.Avalonia.Messages;

// Lightweight message to surface errors to the UI/dialog service
public sealed class UiErrorMessage
{
    public string Title { get; }
    public string Message { get; }
    public UiErrorMessage(string title, string message)
    {
        Title = title;
        Message = message;
    }
}
