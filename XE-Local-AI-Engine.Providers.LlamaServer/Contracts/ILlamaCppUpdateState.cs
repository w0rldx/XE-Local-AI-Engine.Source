namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Thread-safe holder for the latest <see cref="LlamaCppUpdateSnapshot" />. Registered as a singleton so the startup
///     check writes it once and the runtime-status endpoint reads it on demand. The update endpoint also refreshes it
///     after a successful install.
/// </summary>
public interface ILlamaCppUpdateState
{
    /// <summary>The current snapshot (never <see langword="null" />; defaults to <see cref="LlamaCppUpdateSnapshot.Empty" />).</summary>
    LlamaCppUpdateSnapshot Current { get; }

    /// <summary>Atomically replaces the current snapshot with <paramref name="snapshot" />.</summary>
    void Store(LlamaCppUpdateSnapshot snapshot);
}
