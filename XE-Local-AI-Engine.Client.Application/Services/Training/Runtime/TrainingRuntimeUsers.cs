namespace XE_Local_AI_Engine.Client.Services.Training.Runtime;

/// <summary>Counts the gate-free jobs running from the training venv, so a runtime install or remove refuses while one is in flight.</summary>
/// <remarks>
///     A job enters before it rechecks the runtime and keeps its registration until its script run returns. A mutation publishes its
///     phase, then reads <see cref="InUse" />; both sides cross a lock, so either the job sees the phase or the mutation sees the job.
/// </remarks>
public sealed class TrainingRuntimeUsers
{
    private readonly Lock _lock = new();
    private int _count;

    public bool InUse
    {
        get
        {
            lock (_lock)
            {
                return _count > 0;
            }
        }
    }

    /// <summary>Counts one user until the registration is disposed; a repeated dispose is a no-op.</summary>
    public IDisposable Enter()
    {
        lock (_lock)
        {
            _count++;
        }

        return new Registration(this);
    }

    private void Leave()
    {
        lock (_lock)
        {
            _count--;
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly TrainingRuntimeUsers _owner;
        private int _disposed;

        public Registration(TrainingRuntimeUsers owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Leave();
            }
        }
    }
}
