namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>A nullable, versioned descriptor for the active managed source runtime.</summary>
public interface IActiveSourceBuildSignal
{
    GpuVariant? ActiveVariant { get; }
    long Version { get; }
    void SetActive(GpuVariant variant);
    void Clear();
}
