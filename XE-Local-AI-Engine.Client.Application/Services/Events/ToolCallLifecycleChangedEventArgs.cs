namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed class ToolCallLifecycleChangedEventArgs : EventArgs
{
    public ToolCallLifecycleChangedEventArgs(ToolCallLifecyclePayload payload)
    {
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public ToolCallLifecyclePayload Payload { get; }
}
