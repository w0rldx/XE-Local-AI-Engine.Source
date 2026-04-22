namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Events;

public sealed class InvocationCancelledReceivedEventArgs : EventArgs
{
    public InvocationCancelledReceivedEventArgs(InvocationCancelledEvent cancellation)
    {
        Cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
    }

    public InvocationCancelledEvent Cancellation { get; }
}
