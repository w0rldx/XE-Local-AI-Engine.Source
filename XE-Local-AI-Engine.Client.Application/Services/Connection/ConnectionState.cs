namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Enumerates supported worker connection state values.
/// </summary>
public enum WorkerConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Pairing = 4,
    Error = 5
}

/// <summary>
///     Represents connection state.
/// </summary>
public sealed class ConnectionState
{
    private readonly TimeProvider _clock;

    public ConnectionState(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        LastUpdatedAt = _clock.GetUtcNow();
    }

    public WorkerConnectionState Current { get; private set; } = WorkerConnectionState.Disconnected;

    public string? LastError { get; private set; }

    public DateTimeOffset LastUpdatedAt { get; private set; }

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
        LastUpdatedAt = _clock.GetUtcNow();

        StateChanged?.Invoke(this, new WorkerConnectionStateChangedEventArgs(previous, Current, LastError, LastUpdatedAt));
    }
}
