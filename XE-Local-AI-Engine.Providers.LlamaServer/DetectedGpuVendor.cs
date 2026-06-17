namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>GPU vendor detected by the variant-selector probe.</summary>
public enum DetectedGpuVendor
{
    /// <summary>No GPU detected (or detection failed) — CPU floor.</summary>
    None = 0,

    /// <summary>NVIDIA GPU detected.</summary>
    Nvidia = 1,

    /// <summary>AMD GPU detected.</summary>
    Amd = 2,

    /// <summary>Intel GPU detected.</summary>
    Intel = 3
}
