namespace XE_Local_AI_Engine.Client.Services.Connection;

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
