namespace XE_Local_AI_Engine.Services.Connection;

public enum WorkerConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Pairing = 4,
    Error = 5
}

public sealed class ConnectionState
{
    public WorkerConnectionState Current { get; private set; } = WorkerConnectionState.Disconnected;

    public string? LastError { get; private set; }

    public DateTimeOffset LastUpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public event EventHandler<WorkerConnectionStateChangedEventArgs>? StateChanged;

    public void TransitionTo(WorkerConnectionState state, string? error = null)
    {
        if (Current == state && string.Equals(LastError, error, StringComparison.Ordinal))
        {
            return;
        }

        var previous = Current;
        Current = state;
        LastError = error;
        LastUpdatedAt = DateTimeOffset.UtcNow;

        StateChanged?.Invoke(this, new WorkerConnectionStateChangedEventArgs(previous, Current, LastError, LastUpdatedAt));
    }
}

public sealed class WorkerConnectionStateChangedEventArgs : EventArgs
{
    public WorkerConnectionStateChangedEventArgs(WorkerConnectionState previousState,
        WorkerConnectionState currentState,
        string? error,
        DateTimeOffset changedAt)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Error = error;
        ChangedAt = changedAt;
    }

    public WorkerConnectionState PreviousState { get; }

    public WorkerConnectionState CurrentState { get; }

    public string? Error { get; }

    public DateTimeOffset ChangedAt { get; }
}
