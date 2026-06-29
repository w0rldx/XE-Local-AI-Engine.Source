namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     DI wiring for the llama-server local-model provider stack (binary manager + GPU probe/selector + the
///     supervisor + provider seams). Mirrors the <c>AddOllamaLocalModelProvider</c> registration shape.
/// </summary>
public static class LlamaServerServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the model-runtime-core services: the GPU vendor probe, the OS-aware
    ///     variant selector, and the llama.cpp binary manager, plus the supervisor
    ///     (<see cref="ILlamaServerProcessSupervisor" />) and the provider (<c>ILocalModelProvider</c> for
    ///     <c>llamacpp</c>) wired into the multi-provider resolver.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Caller contract:</strong> the consuming application must register a named/typed
    ///         <see cref="System.Net.Http.HttpClient" /> for binary downloads via <c>AddHttpClient</c> (the
    ///         <c>Microsoft.Extensions.Http</c> package is referenced by the Application host, not this provider
    ///         project) and supply an <see cref="IGgufModelStore" /> — the Hugging Face GGUF store
    ///         (<c>AddHuggingFaceGgufStore</c>).
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddLlamaServerLocalModelProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGpuVendorProbe, ProcessGpuVendorProbe>();

        // Cached managed-CUDA signal: a single flag the variant selector reads (no per-call store I/O), set on adopt,
        // cleared on remove / invalid-serve, seeded once at startup. Shared by the selector + the binary manager.
        services.TryAddSingleton<ICudaManagedBuildSignal, CudaManagedBuildSignal>();

        // Operator bring-your-own llama-server override — operator-trust ONLY. Built once from process env vars
        // (XE_LLAMACPP_SERVER_PATH / XE_LLAMACPP_VARIANT) via explicit reads, NEVER from IConfiguration sections, the
        // user-editable node settings store, or a request DTO. Off by default; one instance is shared by the selector and
        // the binary manager so both key off a single source of override truth.
        var overrideOptions = LlamaServerRuntimeOverrideOptions.FromEnvironment();
        services.TryAddSingleton(overrideOptions);

        // The selector takes the override options as a dependency, so register it via an explicit factory (rather than the
        // type-based registration) so the new dependency resolves deterministically.
        services.TryAddSingleton<IGpuVariantSelector>(static sp =>
            new GpuVariantSelector(sp.GetRequiredService<IGpuVendorProbe>(),
                sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<ICudaManagedBuildSignal>()));

        // Dynamic-runtime resolution seams: the live GitHub Releases catalog (tier 1) and the on-disk installed-runtime
        // state (tier 2). The binary manager consults both, falling back to the pinned floor (tier 3) when both miss.
        services.TryAddSingleton<ILlamaCppReleaseCatalog>(static sp =>
            new GitHubLlamaCppReleaseCatalog(sp.GetRequiredService<HttpClient>()));
        services.TryAddSingleton<IInstalledRuntimeStore>(static _ => new InstalledRuntimeStore());

        // Shared "is there a newer runtime?" snapshot — written once by the startup check service and after a successful
        // update install, read by the read-only runtime-status endpoint. Decoupled from any app-package updater channel.
        services.TryAddSingleton<ILlamaCppUpdateState, LlamaCppUpdateState>();

        services.TryAddSingleton<ILlamaCppBinaryManager>(static sp =>
            new LlamaCppBinaryManager(sp.GetRequiredService<HttpClient>(),
                cacheRoot: null,
                activeTag: null,
                sp.GetRequiredService<ILlamaCppReleaseCatalog>(),
                sp.GetRequiredService<IInstalledRuntimeStore>(),
                sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<ICudaManagedBuildSignal>()));

        // In-app Linux CUDA source build (no upstream prebuilt exists): the prerequisite probe, the no-op build-event
        // publisher (the Client host swaps in a hub-backed one), and the single-flight build service. The startup service
        // cleans a stale work dir + seeds the managed-CUDA signal from the installed-runtime record.
        services.TryAddSingleton<ICudaBuildPrerequisiteProbe>(static sp =>
            new CudaBuildPrerequisiteProbe(sp.GetRequiredService<IGpuVendorProbe>()));
        services.TryAddSingleton<ICudaBuildEventPublisher, NullCudaBuildEventPublisher>();
        services.TryAddSingleton<ICudaBuildService>(static sp =>
            new CudaBuildService(sp.GetRequiredService<ICudaBuildPrerequisiteProbe>(),
                sp.GetRequiredService<ILlamaCppBinaryManager>(),
                sp.GetRequiredService<ICudaBuildEventPublisher>(),
                sp.GetRequiredService<ILogger<CudaBuildService>>()));
        services.AddHostedService(static sp => new CudaBuildStartupService(sp.GetRequiredService<ICudaBuildService>(),
            sp.GetRequiredService<IInstalledRuntimeStore>(),
            sp.GetRequiredService<ICudaManagedBuildSignal>(),
            sp.GetRequiredService<ILogger<CudaBuildStartupService>>()));

        // Real available-VRAM probe (Lane B1): parses `llama-server --list-devices`. PLAIN AddSingleton (not TryAdd) so
        // it WINS over the Application-layer TryAddSingleton<IAvailableVramProbe, UnknownAvailableVramProbe>() floor
        // regardless of registration order — TryAdd no-ops once a registration exists, and last-wins resolves to this one.
        services.AddSingleton<IAvailableVramProbe, LlamaListDevicesVramProbe>();

        // Options default here so the supervisor is resolvable; the host overrides them from node config.
        services.TryAddSingleton(new LlamaServerSupervisorOptions());
        services.TryAddSingleton(new LlamaServerExternalEndpointOptions());

        // Process-supervision seams: the OS-aware launcher (tree-kill) + the /health readiness probe.
        services.TryAddSingleton<ILlamaServerProcessLauncher, LlamaServerProcessLauncher>();
        services.TryAddSingleton<ILlamaServerHealthProbe>(static sp =>
            new LlamaServerHealthProbe(sp.GetRequiredService<HttpClient>()));

        // Self-satisfying launch-arg resolver: explore-mode (auto-fit) until the Application host registers its
        // DB-backed IInferenceProfileResolver last (last registration wins), keeping the layer arrow Application →
        // Providers (the interface is DEFINED here, implemented in Application).
        services.TryAddSingleton<IInferenceProfileResolver, DefaultInferenceProfileResolver>();

        // The supervisor owns all llama-server child processes for the node — strictly one singleton. Built via an
        // explicit factory because its ctor is internal (it takes the internal launcher/health-probe seams).
        services.TryAddSingleton(static sp => new LlamaServerProcessSupervisor(sp.GetRequiredService<ILlamaCppBinaryManager>(),
            sp.GetRequiredService<IGpuVariantSelector>(),
            sp.GetRequiredService<IGgufModelStore>(),
            sp.GetRequiredService<ILlamaServerProcessLauncher>(),
            sp.GetRequiredService<ILlamaServerHealthProbe>(),
            sp.GetRequiredService<LlamaServerSupervisorOptions>(),
            sp.GetRequiredService<IInferenceProfileResolver>(),
            sp.GetRequiredService<LlamaServerExternalEndpointOptions>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<ILlamaServerProcessSupervisor>(static sp =>
            sp.GetRequiredService<LlamaServerProcessSupervisor>());

        // SEAM: the llamacpp ILocalModelProvider. Registered over the supervisor + the caller-supplied
        // IGgufModelStore (the Hugging Face GGUF store). Added to the
        // ILocalModelProvider set alongside Ollama; the per-model→provider resolver dispatches across both
        // registrations. Singleton — it holds no per-request state; the deferred chat/embedding
        // clients it hands out own the cold-start.
        services.TryAddSingleton<LlamaServerLocalModelProvider>(static sp =>
            new LlamaServerLocalModelProvider(sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<IGgufModelStore>()));
        services.AddSingleton<ILocalModelProvider>(static sp =>
            sp.GetRequiredService<LlamaServerLocalModelProvider>());

        // Startup orphan reaper: kills stale llama-server processes THIS app left behind on a previous run. A hard host
        // kill (e.g. `aspire stop`) skips the supervisor's graceful DisposeAsync teardown, orphaning the server while it
        // still holds its loopback port + GPU VRAM and so blocking the next start. The reaper matches ONLY binaries under
        // our own llama.cpp cache root, so an unrelated llama-server (e.g. Ollama's) is never touched. Best-effort — it
        // never throws out of StartAsync, so it can never block startup.
        services.TryAddSingleton<IStaleLlamaServerProcessScanner, OsStaleLlamaServerProcessScanner>();
        services.AddHostedService(static sp => new StaleLlamaServerReaper(sp.GetRequiredService<IStaleLlamaServerProcessScanner>(),
            LlamaCppBinaryManager.DefaultLlamaCppBinariesRoot(),
            sp.GetRequiredService<ILogger<StaleLlamaServerReaper>>()));

        // Startup notice: when the bring-your-own override is active, log it once at Warning so it is obvious that an
        // unverified operator-supplied binary is in use (integrity hash verification is skipped). Nothing is logged when
        // the override is unset, so a normal deploy is byte-behavior-unchanged.
        services.AddHostedService(static sp => new LlamaServerRuntimeOverrideStartupNotice(sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
            sp.GetRequiredService<ILogger<LlamaServerRuntimeOverrideStartupNotice>>()));

        return services;
    }
}
