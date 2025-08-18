using CommunityToolkit.Mvvm.Messaging.Messages;

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
