namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ICudaManagedBuildSignal" />: a single volatile flag. Reads/writes are lock-free and
///     thread-safe — the flag is a simple latch read on the variant-selection hot path and flipped by the build service,
///     the remove endpoint, the serve-time validator, and the startup seeder.
/// </summary>
public sealed class CudaManagedBuildSignal : ICudaManagedBuildSignal
{
    private int _activeVariant = -1;
    private long _version;

    /// <inheritdoc />
    public bool IsAvailable => ActiveVariant == GpuVariant.Cuda;

    /// <inheritdoc />
    public GpuVariant? ActiveVariant
    {
        get
        {
            var value = Volatile.Read(ref _activeVariant);
            return value < 0 ? null : (GpuVariant)value;
        }
    }

    /// <inheritdoc />
    public long Version => Interlocked.Read(ref _version);

    /// <inheritdoc />
    public void MarkAvailable()
    {
        SetActive(GpuVariant.Cuda);
    }

    /// <inheritdoc />
    public void SetActive(GpuVariant variant)
    {
        Volatile.Write(ref _activeVariant, (int)variant);
        Interlocked.Increment(ref _version);
    }

    /// <inheritdoc />
    public void Clear()
    {
        Volatile.Write(ref _activeVariant, -1);
        Interlocked.Increment(ref _version);
    }
}
