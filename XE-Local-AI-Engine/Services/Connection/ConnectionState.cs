namespace XE_Local_AI_Engine.Services.Connection
{
    using System;

    public enum WorkerConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Reconnecting = 3,
        Pairing = 4,
        Error = 5,
    }

    public sealed class ConnectionState
    {
        private WorkerConnectionState _current = WorkerConnectionState.Disconnected;

        public event EventHandler<WorkerConnectionStateChangedEventArgs>? StateChanged;

        public WorkerConnectionState Current => _current;

        public string? LastError { get; private set; }

        public DateTimeOffset LastUpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

        public void TransitionTo(WorkerConnectionState state, string? error = null)
        {
            if (_current == state && string.Equals(LastError, error, StringComparison.Ordinal))
            {
                return;
            }

            var previous = _current;
            _current = state;
            LastError = error;
            LastUpdatedAt = DateTimeOffset.UtcNow;

            StateChanged?.Invoke(this, new WorkerConnectionStateChangedEventArgs(previous, _current, LastError, LastUpdatedAt));
        }
    }

    public sealed class WorkerConnectionStateChangedEventArgs : EventArgs
    {
        public WorkerConnectionStateChangedEventArgs(
            WorkerConnectionState previousState,
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
}
