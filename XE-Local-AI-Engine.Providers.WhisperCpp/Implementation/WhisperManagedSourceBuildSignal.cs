namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Default <see cref="IWhisperManagedSourceBuildSignal" />: a lock-free versioned cell. Read on every backend
///     selection, written only by the binary manager when it validates or tombstones a managed runtime record.
/// </summary>
public sealed class WhisperManagedSourceBuildSignal : IWhisperManagedSourceBuildSignal
{
    private int _activeBackend = -1;
    private long _version;

    /// <inheritdoc />
    public WhisperBackend? ActiveBackend
    {
        get
        {
            var value = Volatile.Read(ref _activeBackend);
            return value < 0 ? null : (WhisperBackend)value;
        }
    }

    /// <inheritdoc />
    public long Version => Volatile.Read(ref _version);

    /// <inheritdoc />
    public void SetActive(WhisperBackend backend)
    {
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
        }

        Volatile.Write(ref _activeBackend, (int)backend);
        Interlocked.Increment(ref _version);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Volatile.Write(ref _activeBackend, value: -1);
        Interlocked.Increment(ref _version);
    }
}
