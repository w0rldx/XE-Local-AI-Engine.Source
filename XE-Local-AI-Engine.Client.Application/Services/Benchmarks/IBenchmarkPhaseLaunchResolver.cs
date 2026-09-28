namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Resolves what one benchmark phase will launch with: the profile replay, the KV-cache type it will run with, and
///     the launch identity that vector is INTENDED to produce.
/// </summary>
/// <remarks>
///     Shared by the primary freeze and the judge, because both phases launch the same binary and must agree about
///     what it accepts.
/// </remarks>
public interface IBenchmarkPhaseLaunchResolver
{
    /// <summary>
    ///     What the llama-server binary this node would launch accepts, or <see langword="null" /> when it could not be
    ///     acquired — recorded as "not inspected" rather than failing, so Auto stays on f16 and the spawn reports the
    ///     real acquisition failure.
    /// </summary>
    Task<LlamaServerLaunchCapabilities?> InspectAsync(CancellationToken cancellationToken);

    /// <summary>The variant the inspection settled on, or the selector's answer when nothing was inspected.</summary>
    Task<GpuVariant> SelectVariantAsync(LlamaServerLaunchCapabilities? capabilities, CancellationToken cancellationToken);

    /// <param name="requestedKvCacheType">The type the caller asked for, or <see langword="null" /> for Auto.</param>
    Task<BenchmarkFrozenLaunch> ResolveAsync(string modelName,
        int requiredContextTokens,
        string? requestedKvCacheType,
        LlamaServerLaunchCapabilities? capabilities,
        GpuVariant variant,
        CancellationToken cancellationToken);
}
