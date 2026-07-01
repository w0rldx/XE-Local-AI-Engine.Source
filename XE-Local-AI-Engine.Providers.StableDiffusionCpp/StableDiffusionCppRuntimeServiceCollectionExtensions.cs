namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     DI wiring for the Lane B <c>sd-server</c> runtime adapter: the process launcher, the readiness probe, the resident
///     process supervisor, the typed HTTP job client, and the <see cref="IImageRuntime" /> facade. Companion to
///     <see cref="StableDiffusionCppServiceCollectionExtensions.AddStableDiffusionCppImageProvider" /> (Lane A) — it
///     consumes Lane A's seams (<see cref="Contracts.ISdGpuBackendSelector" />,
///     <see cref="Contracts.IStableDiffusionBinaryManager" />) plus the image model store
///     (<see cref="IImageModelStore" />), so both must be registered first.
/// </summary>
public static class StableDiffusionCppRuntimeServiceCollectionExtensions
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient" /> for sd-server job/readiness HTTP (loopback, short-lived per call).</summary>
    public const string RuntimeHttpClientName = "sdcpp-runtime";

    /// <summary>
    ///     Registers the sd-server runtime adapter (Lane B). All registrations are <c>TryAdd</c> so a host may override any
    ///     seam. Requires the Lane A provider (<c>AddStableDiffusionCppImageProvider</c>) and an
    ///     <see cref="IImageModelStore" /> to be registered.
    /// </summary>
    public static IServiceCollection AddStableDiffusionCppImageRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Runtime supervision options default here so the supervisor is resolvable; the host may override from node config.
        services.TryAddSingleton(new StableDiffusionRuntimeOptions());

        // Loopback HTTP for job submit/poll/cancel + readiness — mirrors how llama registers its runtime HttpClient.
        services.AddHttpClient(RuntimeHttpClientName);

        // OS-aware process launcher (Windows Job Object / Linux setsid tree-kill).
        services.TryAddSingleton<IImageServerProcessLauncher, ImageServerProcessLauncher>();

        services.TryAddSingleton<IImageServerReadinessProbe>(static sp =>
            new ImageServerReadinessProbe(sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName)));

        services.TryAddSingleton(static sp =>
            new SdServerJobClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName)));

        // The supervisor owns every resident sd-server child process for the node — strictly one singleton. Built via an
        // explicit factory because its ctor is internal (it takes the internal launcher/readiness seams).
        services.TryAddSingleton(static sp => new ImageServerProcessSupervisor(
            sp.GetRequiredService<IImageModelStore>(),
            sp.GetRequiredService<ISdGpuBackendSelector>(),
            sp.GetRequiredService<IStableDiffusionBinaryManager>(),
            sp.GetRequiredService<IImageServerProcessLauncher>(),
            sp.GetRequiredService<IImageServerReadinessProbe>(),
            sp.GetRequiredService<StableDiffusionRuntimeOptions>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IImageServerSupervisor>(static sp => sp.GetRequiredService<ImageServerProcessSupervisor>());

        // The public image-generation facade. Singleton — it holds no per-request state.
        services.TryAddSingleton<IImageRuntime>(static sp =>
            new StableDiffusionCppRuntime(sp.GetRequiredService<IImageServerSupervisor>(),
                sp.GetRequiredService<SdServerJobClient>()));

        return services;
    }
}
