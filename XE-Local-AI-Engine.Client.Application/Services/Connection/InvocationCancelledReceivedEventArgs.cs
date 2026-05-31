namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Event payload for invocation cancelled received notifications.
/// </summary>
public sealed class InvocationCancelledReceivedEventArgs : EventArgs
{
    public InvocationCancelledReceivedEventArgs(InvocationCancelledEvent cancellation)
    {
        Cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
    }

    public InvocationCancelledEvent Cancellation { get; }
}
