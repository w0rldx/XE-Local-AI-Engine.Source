namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Event payload for invocation state changed notifications.
/// </summary>
public sealed class InvocationStateChangedEventArgs : EventArgs
{
    public InvocationStateChangedEventArgs(InvocationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        State = state;
    }

    public InvocationState State { get; }
}
