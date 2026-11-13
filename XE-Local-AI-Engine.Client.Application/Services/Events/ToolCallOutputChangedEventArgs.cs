namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Event payload for live tool command output notifications.
/// </summary>
public sealed class ToolCallOutputChangedEventArgs : EventArgs
{
    public ToolCallOutputChangedEventArgs(ToolCallOutputPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Payload = payload;
    }

    public ToolCallOutputPayload Payload { get; }
}
