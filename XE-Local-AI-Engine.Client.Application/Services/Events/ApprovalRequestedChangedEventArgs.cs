namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Event payload for tool-approval-requested notifications. Mirrors <see cref="ToolCallLifecycleChangedEventArgs" />
///     so the local chat stream can fan a pending approval out as an <c>approval-requested</c> stream event.
/// </summary>
public sealed class ApprovalRequestedChangedEventArgs : EventArgs
{
    public ApprovalRequestedChangedEventArgs(ApprovalLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Payload = payload;
    }

    public ApprovalLifecyclePayload Payload { get; }
}
