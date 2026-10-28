namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.CompilerServices;

/// <summary>
///     Default <see cref="IManagedSourceBuildSignal{TBackend}" />: a lock-free versioned cell. Read on every backend
///     selection, written only by the binary manager when it validates or tombstones a managed runtime record.
/// </summary>
public sealed class ManagedSourceBuildSignal<TBackend> : IManagedSourceBuildSignal<TBackend>
    where TBackend : struct, Enum
{
    private StrongBox<TBackend>? _activeBackend;
    private long _version;

    /// <inheritdoc />
    public TBackend? ActiveBackend => Volatile.Read(ref _activeBackend)?.Value;

    /// <inheritdoc />
    public long Version => Volatile.Read(ref _version);

    /// <inheritdoc />
    public void SetActive(TBackend backend)
    {
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
        }

        Volatile.Write(ref _activeBackend, new StrongBox<TBackend>(backend));
        Interlocked.Increment(ref _version);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Volatile.Write(ref _activeBackend, value: null);
        Interlocked.Increment(ref _version);
    }
}
