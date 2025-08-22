using CommunityToolkit.Mvvm.Messaging.Messages;
using Furchive.Core.Models;

namespace Furchive.Avalonia.Messages;

// Minimal marker messages for WeakReferenceMessenger wiring in Avalonia layer
public sealed class PoolsCacheRebuiltMessage : ValueChangedMessage<bool>
{
    public PoolsCacheRebuiltMessage(bool value = true) : base(value) { }
}

public sealed class PoolsCacheRebuildRequestedMessage : ValueChangedMessage<bool>
{
    public PoolsCacheRebuildRequestedMessage(bool value = true) : base(value) { }
}

public sealed class PoolsSoftRefreshRequestedMessage : ValueChangedMessage<bool>
{
    public PoolsSoftRefreshRequestedMessage(bool value = true) : base(value) { }
}

public sealed class SettingsSavedMessage : ValueChangedMessage<bool>
{
    public SettingsSavedMessage(bool value = true) : base(value) { }
}

public sealed class OpenViewerMessage : ValueChangedMessage<MediaItem>
{
    public OpenViewerMessage(MediaItem item) : base(item) { }
}

// New richer viewer open request including navigation context (list snapshot + current index)
public sealed class OpenViewerRequestMessage : ValueChangedMessage<OpenViewerRequest>
{
    public OpenViewerRequestMessage(OpenViewerRequest value) : base(value) { }
}

public sealed record OpenViewerRequest(IReadOnlyList<MediaItem> Items, int Index);
