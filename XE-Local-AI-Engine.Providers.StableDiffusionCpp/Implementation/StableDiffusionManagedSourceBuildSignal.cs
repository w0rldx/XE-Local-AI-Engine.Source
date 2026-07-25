namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class StableDiffusionManagedSourceBuildSignal : IStableDiffusionManagedSourceBuildSignal
{
    private int _activeBackend = -1;
    private long _version;

    public SdGpuBackend? ActiveBackend
    {
        get
        {
            var value = Volatile.Read(ref _activeBackend);
            return value < 0 ? null : (SdGpuBackend)value;
        }
    }

    public long Version => Volatile.Read(ref _version);

    public void SetActive(SdGpuBackend backend)
    {
        if (!Enum.IsDefined(backend))
        {
            throw new ArgumentOutOfRangeException(nameof(backend), backend, null);
        }

        Volatile.Write(ref _activeBackend, (int)backend);
        Interlocked.Increment(ref _version);
    }

    public void Clear()
    {
        Volatile.Write(ref _activeBackend, -1);
        Interlocked.Increment(ref _version);
    }
}
