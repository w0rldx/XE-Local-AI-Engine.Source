namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     DI wiring for the <c>sd-server</c> runtime adapter: the process launcher, the readiness probe, the resident
///     process supervisor, the typed HTTP job client, and the <see cref="IImageRuntime" /> facade.
/// </summary>
/// <remarks>
///     Companion to <see cref="StableDiffusionCppServiceCollectionExtensions.AddStableDiffusionCppImageProvider" />: it
///     consumes that provider's seams (<see cref="Contracts.ISdGpuBackendSelector" />,
///     <see cref="Contracts.IStableDiffusionBinaryManager" />) plus the image model store
///     (<see cref="IImageModelStore" />), so both must be registered first.
/// </remarks>
public static class StableDiffusionCppRuntimeServiceCollectionExtensions
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient" /> for sd-server job/readiness HTTP (loopback, short-lived per call).</summary>
    public const string RuntimeHttpClientName = "sdcpp-runtime";

    /// <summary>The process name the reaper scans for and the spawn-receipt directory under the node data root.</summary>
    private const string ServerName = "sd-server";

    /// <summary>
    ///     Registers the sd-server runtime adapter. All registrations are <c>TryAdd</c> so a host may override any
    ///     seam. Requires <c>AddStableDiffusionCppImageProvider</c> to be registered first, plus an
    ///     <see cref="IImageModelStore" /> to be registered.
    /// </summary>
    public static IServiceCollection AddStableDiffusionCppImageRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Runtime supervision options default here so the supervisor is resolvable; the host may override from node config.
        services.TryAddSingleton(new StableDiffusionRuntimeOptions());

        // Loopback HTTP for job submit/poll/cancel + readiness. Job submit is a POST with no idempotency key, so this owns a single POST-safe
        // pipeline instead of Aspire's retry-everything default. See docs/wiki/14-image-generation.md ("The runtime HTTP client and its retry contract").
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers / DisableForUnsafeHttpMethods are experimental; used deliberately to own a single, POST-safe pipeline.
        services.AddHttpClient(RuntimeHttpClientName)
                .RemoveAllResilienceHandlers()
                .AddStandardResilienceHandler()
                .Configure(static options => options.Retry.DisableForUnsafeHttpMethods());
#pragma warning restore EXTEXP0001

        // Carries the fine sampling progress the launcher parses out of the daemon's stdout to whichever generation is
        // listening. Singleton and registered before the launcher that publishes into it.
        services.TryAddSingleton<IImageServerProgressBroker, ImageServerProgressBroker>();

        // OS-aware process launcher (Windows Job Object / Linux setsid tree-kill).
        services.AddChildProcessOutputTails();
        services.TryAddSingleton<IImageServerProcessLauncher, ImageServerProcessLauncher>();

        services.TryAddSingleton<IImageServerReadinessProbe>(static sp =>
            new ImageServerReadinessProbe(sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName)));

        services.TryAddSingleton(static sp =>
            new SdServerJobClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(RuntimeHttpClientName)));

        // GPU-load admission floor: a no-op serializer so an image-only host resolves the gate. The composition
        // root overrides it (plain AddSingleton, last-wins) with the real singleton shared with the llama-server supervisor.
        services.TryAddSingleton<IGpuModelLoadAdmission, NoOpGpuModelLoadAdmission>();

        // ONE sd-server spawn-receipt store, keyed by server name: the supervisor writes the receipts the startup reaper reads back. A provider-only
        // host registers no INodeDataDirectory, and then receipts are off and only the binaries-root reap applies.
        services.TryAddKeyedSingleton(ServerName, static (sp, _) =>
            new ProcessSpawnReceiptStore(sp.GetService<INodeDataDirectory>()?.Root, ServerName, sp.GetRequiredService<ILogger<ProcessSpawnReceiptStore>>()));

        // The supervisor owns every resident sd-server child process for the node — strictly one singleton. Built via an
        // explicit factory because its ctor is internal (it takes the internal launcher/readiness seams).
        services.TryAddSingleton(static sp => new ImageServerProcessSupervisor(sp.GetRequiredService<IImageModelStore>(),
            sp.GetRequiredService<ISdGpuBackendSelector>(),
            sp.GetRequiredService<IStableDiffusionBinaryManager>(),
            sp.GetRequiredService<IImageServerProcessLauncher>(),
            sp.GetRequiredService<IImageServerReadinessProbe>(),
            sp.GetRequiredService<StableDiffusionRuntimeOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ImageServerProcessSupervisor>>(),
            sp.GetRequiredService<IGpuModelLoadAdmission>(),
            sp.GetRequiredService<IImageRuntimeActivityGate>(),
            sp.GetRequiredKeyedService<ProcessSpawnReceiptStore>(ServerName),
            sp.GetRequiredService<IRuntimeResidencyChangeNotifier>()));
        services.TryAddSingleton<IImageServerSupervisor>(static sp => sp.GetRequiredService<ImageServerProcessSupervisor>());

        // The public image-generation facade. Singleton — it holds no per-request state.
        services.TryAddSingleton<IImageRuntime>(static sp =>
            new StableDiffusionCppRuntime(sp.GetRequiredService<IImageServerSupervisor>(),
                sp.GetRequiredService<SdServerJobClient>(),
                sp.GetRequiredService<IImageServerProgressBroker>(),
                sp.GetRequiredService<ILogger<StableDiffusionCppRuntime>>()));

        // Startup orphan reaper (never throws): sd-server binaries under our own cache root, plus on Linux any child this node's spawn receipts still identify.
        // AddSingleton, not AddHostedService: every runtime registers the shared StaleProcessReaper type, and AddHostedService dedupes by implementation type, so only the first would run.
        services.AddSingleton<IHostedService, StaleProcessReaper>(static sp => new StaleProcessReaper(new OsStaleProcessScanner("sd-server"),
            StableDiffusionCppBinaryManager.DefaultStableDiffusionBinariesRoot(),
            "sd-server",
            logExecutablePath: true,
            sp.GetRequiredService<ILogger<StaleProcessReaper>>(),
            sp.GetRequiredKeyedService<ProcessSpawnReceiptStore>(ServerName)));

        return services;
    }
}
