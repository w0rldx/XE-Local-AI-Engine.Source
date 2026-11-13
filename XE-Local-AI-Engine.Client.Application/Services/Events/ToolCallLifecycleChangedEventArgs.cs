namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Event payload for tool call lifecycle changed notifications.
/// </summary>
public sealed class ToolCallLifecycleChangedEventArgs : EventArgs
{
    public ToolCallLifecycleChangedEventArgs(ToolCallLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Payload = payload;
    }

    public ToolCallLifecyclePayload Payload { get; }
}
