namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     whisper.cpp acceleration backend selected for the host or managed runtime.
/// </summary>
/// <remarks>
///     No Vulkan/Metal/OpenCL member exists: those backends are a V1 non-goal. NVIDIA maps to <see cref="Cuda" /> on
///     Windows only, where a cuBLAS prebuilt ships; Linux has no CUDA prebuilt, so its hardware-selection path is
///     <see cref="Cpu" /> unless a managed source build or a bring-your-own override selects CUDA explicitly.
/// </remarks>
public enum WhisperBackend
{
    /// <summary>CPU-only build. The universal floor available on every supported OS/arch.</summary>
    Cpu = 0,

    /// <summary>NVIDIA CUDA build (Windows cuBLAS prebuilt, or a Linux managed/source override).</summary>
    Cuda = 1
}
