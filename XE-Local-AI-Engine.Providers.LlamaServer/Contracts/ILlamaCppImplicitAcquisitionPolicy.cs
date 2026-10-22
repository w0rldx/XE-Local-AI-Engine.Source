namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Decides whether a launch may download a llama.cpp runtime nobody asked for. An explicit operator install bypasses
///     it (<see cref="ILlamaCppBinaryManager.AcquireBinaryAsync(GpuVariant, CancellationToken)" />).
/// </summary>
public interface ILlamaCppImplicitAcquisitionPolicy
{
    /// <summary>The sanitized reason the download is refused, or <see langword="null" /> when it may run.</summary>
    Task<string?> GetRefusalAsync(CancellationToken ct);
}
