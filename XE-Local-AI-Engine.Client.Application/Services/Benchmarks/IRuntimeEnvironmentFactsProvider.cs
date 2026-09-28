namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Providers.LlamaServer;

public interface IRuntimeEnvironmentFactsProvider
{
    /// <summary>
    ///     Captures the environment facts for a spawn of <paramref name="variant" />. Never throws for a missing or
    ///     unreadable part (that part is <see langword="null" /> and named in
    ///     <see cref="RuntimeEnvironmentFactsV1.Missing" />); cancellation still propagates.
    /// </summary>
    Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct);
}
