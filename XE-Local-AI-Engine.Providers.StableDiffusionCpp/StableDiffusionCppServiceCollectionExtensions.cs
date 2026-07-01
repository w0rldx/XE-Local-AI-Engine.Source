namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Configuration;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     DI wiring for the stable-diffusion.cpp image runtime infrastructure Lane A owns: the pinned binary manager, the
///     GPU-backend selector (over the shared <see cref="IHardwareProfiler" />), the bring-your-own override, and the
///     runtime options. Mirrors the ordered registration of <c>AddLlamaServerLocalModelProvider</c>.
/// </summary>
/// <remarks>
///     <strong>Scope:</strong> this registers only what Lane A owns. The <c>sd-server</c> supervisor/runtime adapter
///     (Lane B) and the job coordinator / encrypted image store / hub (Lane C) are wired separately and consume these
///     seams — chiefly <see cref="IStableDiffusionBinaryManager" /> (call
///     <see cref="IStableDiffusionBinaryManager.EnsureBinaryAsync" />) and <see cref="ISdGpuBackendSelector" />.
///     <para>
///         <strong>Caller contract:</strong> the consuming application must register an <see cref="IHardwareProfiler" />
///         (the shared hardware probe lives in <c>Providers.Capabilities</c>) before resolving the selector.
///     </para>
/// </remarks>
public static class StableDiffusionCppServiceCollectionExtensions
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient" /> for stable-diffusion.cpp prebuilt-binary downloads.</summary>
    public const string BinaryHttpClientName = "sdcpp-binary";

    /// <summary>
    ///     Registers the stable-diffusion.cpp image-runtime infrastructure Lane A owns (binary manager, backend selector,
    ///     bring-your-own override, runtime options). All registrations are <c>TryAdd</c> so a host may override any seam.
    /// </summary>
    public static IServiceCollection AddStableDiffusionCppImageProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Bring-your-own override is built once from the operator-trust env channel (never from IConfiguration/DTOs).
        var overrideOptions = StableDiffusionServerRuntimeOverrideOptions.FromEnvironment();
        services.TryAddSingleton(overrideOptions);

        services.TryAddSingleton(new StableDiffusionRuntimeOptions());

        services.AddHttpClient(BinaryHttpClientName);

        services.TryAddSingleton<ISdGpuBackendSelector>(static sp =>
            new SdGpuBackendSelector(sp.GetRequiredService<IHardwareProfiler>(),
                sp.GetRequiredService<StableDiffusionServerRuntimeOverrideOptions>()));

        services.TryAddSingleton<IStableDiffusionBinaryManager>(static sp =>
            new StableDiffusionCppBinaryManager(sp.GetRequiredService<IHttpClientFactory>().CreateClient(BinaryHttpClientName),
                cacheRoot: null,
                activeTag: null,
                sp.GetRequiredService<StableDiffusionServerRuntimeOverrideOptions>()));

        return services;
    }
}
