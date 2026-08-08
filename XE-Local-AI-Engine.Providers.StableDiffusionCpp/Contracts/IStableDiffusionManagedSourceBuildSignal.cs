namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Versioned in-memory signal for the desired managed source backend.</summary>
public interface IStableDiffusionManagedSourceBuildSignal
{
    SdGpuBackend? ActiveBackend { get; }
    long Version { get; }
    void SetActive(SdGpuBackend backend);
    void Clear();
}
