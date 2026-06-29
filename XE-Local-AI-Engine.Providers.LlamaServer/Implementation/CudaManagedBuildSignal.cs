namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ICudaManagedBuildSignal" />: a single volatile flag. Reads/writes are lock-free and
///     thread-safe — the flag is a simple latch read on the variant-selection hot path and flipped by the build service,
///     the remove endpoint, the serve-time validator, and the startup seeder.
/// </summary>
public sealed class CudaManagedBuildSignal : ICudaManagedBuildSignal
{
    private volatile bool _available;

    /// <inheritdoc />
    public bool IsAvailable => _available;

    /// <inheritdoc />
    public void MarkAvailable()
    {
        _available = true;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _available = false;
    }
}
