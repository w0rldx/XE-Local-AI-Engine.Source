namespace XE_Local_AI_Engine.Client.Services.Training;

/// <summary>
///     Process-wide "a training run owns this node" flag (plan decision #13). A training run acquires it for its whole
///     duration; dataset generation, benchmarks and image jobs check <see cref="IsActive" /> before starting, and a run
///     start refuses while they are active. Slice 1 only consumes the read side — the generation queue refuses to claim
///     work while a run holds it; the acquire side is here so Slice 3 has one seam to take, not two.
/// </summary>
public interface ITrainingActivity
{
    bool IsActive { get; }

    /// <summary>
    ///     Takes the exclusive flag, or returns <see langword="null" /> when another holder already owns it. Disposing the
    ///     returned handle releases it; disposing twice is a no-op.
    /// </summary>
    IDisposable? TryBegin();
}

/// <inheritdoc />
public sealed class TrainingActivity : ITrainingActivity
{
    private int _active;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public IDisposable? TryBegin() =>
        Interlocked.CompareExchange(ref _active, 1, 0) == 0 ? new Handle(this) : null;

    private sealed class Handle(TrainingActivity owner) : IDisposable
    {
        private TrainingActivity? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } released)
            {
                Volatile.Write(ref released._active, 0);
            }
        }
    }
}
