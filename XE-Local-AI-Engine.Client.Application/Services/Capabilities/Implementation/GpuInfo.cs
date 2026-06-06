namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

/// <summary>Detected GPU facts used when composing capability reports.</summary>
/// <param name="GpuName">GPU model name as reported by nvidia-smi.</param>
/// <param name="VramMb">Total VRAM in MB, when parseable.</param>
/// <param name="CudaAvailable">True once a CUDA-capable GPU has been confirmed.</param>
internal sealed record GpuInfo(string GpuName, long? VramMb, bool CudaAvailable);
