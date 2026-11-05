namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default in-memory <see cref="ILlamaCppUpdateState" />. A single reference field swapped under <c>Volatile</c>
///     read/write — reads are lock-free and writes are atomic (reference assignment), so the startup writer and endpoint
///     readers never tear a snapshot.
/// </summary>
public sealed class LlamaCppUpdateState : ILlamaCppUpdateState
{
    private LlamaCppUpdateSnapshot _current = LlamaCppUpdateSnapshot.Empty;

    public LlamaCppUpdateSnapshot Current => Volatile.Read(ref _current);

    public void Store(LlamaCppUpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
