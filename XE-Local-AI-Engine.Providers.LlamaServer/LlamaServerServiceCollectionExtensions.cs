namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
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
        services.TryAddSingleton<ITokenEstimatorCalibrationStore, TokenEstimatorCalibrationStore>();
        services.TryAddSingleton<ITokenEstimatorCalibrationScheduler, NullTokenEstimatorCalibrationScheduler>();

        services.TryAddSingleton<IGpuVendorProbe, ProcessGpuVendorProbe>();

        // Cached managed-CUDA signal: a single flag the variant selector reads (no per-call store I/O), set on adopt,
        // cleared on remove / invalid-serve, seeded once at startup. Shared by the selector + the binary manager.
        services.TryAddSingleton<ICudaManagedBuildSignal, CudaManagedBuildSignal>();
        services.TryAddSingleton<IActiveSourceBuildSignal>(static sp => sp.GetRequiredService<ICudaManagedBuildSignal>());

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

        // First-run runtime-acquisition visibility. The no-op publisher keeps provider-only / headless / CI hosts silent
        // and byte-behavior-identical; the Client host swaps in a hub-backed one. The manager depends on the REGISTRY,
        // never on the publisher directly: the registry is what stamps the monotonic sequence the late-join hydrate
        // reconciles against, so routing every write through it makes "recorded but never broadcast" unrepresentable.
        services.TryAddSingleton<IRuntimeAcquisitionEventPublisher, NullRuntimeAcquisitionEventPublisher>();
        services.TryAddSingleton<IRuntimeAcquisitionStatusRegistry, RuntimeAcquisitionStatusRegistry>();

        services.TryAddSingleton<ILlamaCppBinaryManager>(static sp =>
            new LlamaCppBinaryManager(sp.GetRequiredService<HttpClient>(),
                cacheRoot: null,
                activeTag: null,
                sp.GetRequiredService<ILlamaCppReleaseCatalog>(),
                sp.GetRequiredService<IInstalledRuntimeStore>(),
                sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<ICudaManagedBuildSignal>(),
                sp.GetRequiredService<IRuntimeAcquisitionStatusRegistry>()));

        // In-app Linux CUDA source build (no upstream prebuilt exists): the prerequisite probe, the no-op build-event
        // publisher (the Client host swaps in a hub-backed one), and the single-flight build service. The startup service
        // cleans a stale work dir + seeds the managed-CUDA signal from the installed-runtime record.
        services.TryAddSingleton<ICudaBuildPrerequisiteProbe>(static sp =>
            new CudaBuildPrerequisiteProbe(sp.GetRequiredService<IGpuVendorProbe>()));
        services.TryAddSingleton<ICudaBuildEventPublisher, NullCudaBuildEventPublisher>();
        services.TryAddSingleton<ILlamaCppSourceBuildPrerequisiteProbe>(static sp =>
            new LlamaCppSourceBuildPrerequisiteProbe(sp.GetRequiredService<IGpuVendorProbe>()));
        services.TryAddSingleton<ILlamaCppSourceBuildEventPublisher, NullLlamaCppSourceBuildEventPublisher>();
        services.TryAddSingleton<ILlamaCppSourceBuildActivity, LlamaCppSourceBuildActivity>();

        // Conversion tooling for training exports. Provisioned lazily on the first export, never at startup — a node
        // that never trains never fetches it.
        services.TryAddSingleton<IConvertScriptSourceFetcher, GitConvertScriptSourceFetcher>();
        services.TryAddSingleton<IConvertScriptProvisioner, ConvertScriptProvisioner>();

        // Real llama.cpp process-VRAM-budget probe: parses `llama-server --list-devices`. PLAIN AddSingleton (not TryAdd) so
        // it WINS over the Application-layer TryAddSingleton<IProcessVramBudgetProbe, UnknownProcessVramBudgetProbe>() floor
        // regardless of registration order — TryAdd no-ops once a registration exists, and last-wins resolves to this one.
        services.AddSingleton<IProcessVramBudgetProbe, LlamaListDevicesProcessVramBudgetProbe>();

        // Device-inventory probe: parses `llama-server --list-devices` into a structured {variant, devices[]}
        // (sharing the process runner with the VRAM probe), cached per resolved binary. The Application-layer runtime
        // device audit consumes it to detect a GPU-variant binary that enumerates zero devices (a silent CPU fallback).
        services.TryAddSingleton<ILlamaDeviceInventoryProbe, LlamaDeviceInventoryProbe>();

        // Probe the resolved executable rather than inferring flags from a tag. The successful --version/--help result
        // is cached per requested-version/path/length/mtime/SHA-256 identity and gates every final launch vector,
        // including BYO/source builds.
        services.TryAddSingleton<ILlamaServerCapabilityManifestProbe, LlamaServerCapabilityManifestProbe>();

        // The public question-answering seam over that same probe, for callers outside this provider that must settle a
        // launch vector BEFORE a spawn exists (the benchmark freeze). It exposes neither the manifest nor the resolved
        // binary, so no path crosses the boundary.
        services.TryAddSingleton<ILlamaServerLaunchCapabilityInspector>(static sp =>
            new LlamaServerLaunchCapabilityInspector(sp.GetRequiredService<IGpuVariantSelector>(),
                sp.GetRequiredService<ILlamaCppBinaryManager>(),
                sp.GetRequiredService<ILlamaServerCapabilityManifestProbe>()));

        // GPU-load admission floor: a no-op serializer so a provider-only host resolves the gate even when the
        // application layer has not registered the real, metric-emitting serializer. The composition root overrides this
        // with a plain AddSingleton (last-wins) so both the LLM and image supervisors share ONE process-wide gate.
        services.TryAddSingleton<IGpuModelLoadAdmission, NoOpGpuModelLoadAdmission>();

        // Options default here so the supervisor is resolvable; the host overrides them from node config.
        services.TryAddSingleton(new LlamaServerSupervisorOptions());
        services.TryAddSingleton(new LlamaServerExternalEndpointOptions());
        services.TryAddSingleton<ILlamaServerEndpointBinding, LlamaServerEndpointBinding>();

        // The central launch policy (deterministic -c per role, GPU KV-cache quant + flash attention,
        // CPU threads) plus its persistent safe-fallback store. Options default here; the host overrides from node config.
        services.TryAddSingleton(new LlamaServerLaunchPolicyOptions());
        services.TryAddSingleton<IProcessLaunchAdmissionRegistry, ProcessLaunchAdmissionRegistry>();
        services.TryAddSingleton<IProcessContextAllocationResolver>(static sp =>
            new DefaultProcessContextAllocationResolver(sp.GetRequiredService<LlamaServerLaunchPolicyOptions>()));
        services.TryAddSingleton<ILlamaServerLaunchFallbackStore>(static sp =>
            new LlamaServerLaunchFallbackStore(cacheRoot: null, sp.GetRequiredService<ILogger<LlamaServerLaunchFallbackStore>>()));
        services.TryAddSingleton<ILlamaServerLaunchPolicy>(static sp =>
            new LlamaServerLaunchPolicy(sp.GetRequiredService<LlamaServerLaunchPolicyOptions>(),
                sp.GetRequiredService<ILlamaServerLaunchFallbackStore>(),
                sp.GetRequiredService<ILogger<LlamaServerLaunchPolicy>>()));

        // Process-supervision seams: the OS-aware launcher (tree-kill) + the /health readiness probe.
        services.TryAddSingleton<ILlamaServerProcessLauncher, LlamaServerProcessLauncher>();

        // The readiness/liveness probe gets a DEDICATED HttpClient that bypasses the app's IHttpClientFactory,
        // so it never inherits the standard resilience handler's exponential retries — the audited cause of a single
        // logical probe firing at +0.2/2.4/5.1/10.2 s and detecting readiness up to ~5 s late. The probe issues exactly
        // one bounded request per poll (its own per-attempt timeout), and the supervisor's 250 ms cadence controls
        // timing. Localhost-only, so factory handler rotation is unnecessary; a modest backstop Timeout sits above the
        // probe's own per-attempt/reuse bounds. Process-lifetime singleton — intentionally never disposed.
        services.TryAddSingleton<ILlamaServerHealthProbe>(static _ =>
            new LlamaServerHealthProbe(new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            }));

        // Path-addressed throwaway spawn for the training export smoke gate. Explicit factory for the same reason the
        // supervisor needs one: it takes the internal launcher/health-probe seams.
        services.TryAddSingleton<TransientLlamaServerLauncher>(static sp =>
            new TransientLlamaServerLauncher(sp.GetRequiredService<ILlamaCppBinaryManager>(),
                sp.GetRequiredService<IGpuVariantSelector>(),
                sp.GetRequiredService<ILlamaServerProcessLauncher>(),
                sp.GetRequiredService<ILlamaServerHealthProbe>(),
                sp.GetRequiredService<ILogger<TransientLlamaServerLauncher>>()));
        services.TryAddSingleton<ITransientLlamaServerLauncher>(static sp =>
            sp.GetRequiredService<TransientLlamaServerLauncher>());

        // Self-satisfying launch-arg resolver: explore-mode (auto-fit) until the Application host registers its
        // DB-backed IInferenceProfileResolver last (last registration wins), keeping the layer arrow Application →
        // Providers (the interface is DEFINED here, implemented in Application).
        services.TryAddSingleton<IInferenceProfileResolver, DefaultInferenceProfileResolver>();

        // Self-satisfying per-model extra-launch-arg resolver: empty (no override) until the Application host registers
        // its store-backed resolver last (last registration wins), keeping the layer arrow Application → Providers (the
        // interface is DEFINED here, implemented in Application).
        services.TryAddSingleton<ILlamaServerExtraLaunchArgumentsResolver, EmptyLlamaServerExtraLaunchArgumentsResolver>();

        // Measured GPU layer placement for the node: the supervisor writes it as models load, the runtime device audit
        // reads it for the operator UI. Both must see the SAME instance, so it is registered before the supervisor and
        // passed in explicitly rather than left to the supervisor's private default.
        services.TryAddSingleton<ILlamaLayerPlacementReport, LlamaLayerPlacementReport>();

        // Provider-only hosts remain self-satisfying. The application host overrides this report-only seam with its
        // shared NodeMetrics bridge; it never participates in admission or memory accounting.
        services.TryAddSingleton<ILlamaServerLoadTelemetry, NullLlamaServerLoadTelemetry>();

        // The supervisor owns all llama-server child processes for the node — strictly one singleton. Built via an
        // explicit factory because its ctor is internal (it takes the internal launcher/health-probe seams).
        services.TryAddSingleton(static sp => new LlamaServerProcessSupervisor(sp.GetRequiredService<ILlamaCppBinaryManager>(),
            sp.GetRequiredService<IGpuVariantSelector>(),
            sp.GetRequiredService<IGgufModelStore>(),
            sp.GetRequiredService<ILlamaServerProcessLauncher>(),
            sp.GetRequiredService<ILlamaServerHealthProbe>(),
            sp.GetRequiredService<ILlamaServerCapabilityManifestProbe>(),
            sp.GetRequiredService<LlamaServerSupervisorOptions>(),
            sp.GetRequiredService<IInferenceProfileResolver>(),
            sp.GetRequiredService<ILlamaServerLaunchPolicy>(),
            sp.GetRequiredService<LlamaServerExternalEndpointOptions>(),
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<ILogger<LlamaServerProcessSupervisor>>(),
            sp.GetRequiredService<IGpuModelLoadAdmission>(),
            sp.GetRequiredService<ILlamaCppSourceBuildActivity>(),
            allocationResolver: sp.GetRequiredService<IProcessContextAllocationResolver>(),
            layerPlacementReport: sp.GetRequiredService<ILlamaLayerPlacementReport>(),
            launchAdmissions: sp.GetRequiredService<IProcessLaunchAdmissionRegistry>(),
            extraArgumentsResolver: sp.GetRequiredService<ILlamaServerExtraLaunchArgumentsResolver>(),
            loadTelemetry: sp.GetRequiredService<ILlamaServerLoadTelemetry>()));
        services.TryAddSingleton<ILlamaServerProcessSupervisor>(static sp =>
            sp.GetRequiredService<LlamaServerProcessSupervisor>());
        services.TryAddSingleton<ITransientLlamaServerEvaluationHarness>(static sp =>
            new TransientLlamaServerEvaluationHarness(sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<ILlamaCppBinaryManager>(),
                sp.GetRequiredService<IGpuVariantSelector>(),
                sp.GetRequiredService<ILlamaServerCapabilityManifestProbe>(),
                sp.GetRequiredService<ILlamaServerLaunchPolicy>(),
                sp.GetRequiredService<TransientLlamaServerLauncher>(),
                sp.GetRequiredService<IGpuModelLoadAdmission>()));

        services.TryAddSingleton<ILlamaCppSourceBuildService>(static sp =>
            new LlamaCppSourceBuildService(sp.GetRequiredService<ILlamaCppSourceBuildPrerequisiteProbe>(),
                sp.GetRequiredService<ILlamaCppBinaryManager>(),
                sp.GetRequiredService<IInstalledRuntimeStore>(),
                sp.GetRequiredService<IActiveSourceBuildSignal>(),
                sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<ILlamaCppSourceBuildActivity>(),
                sp.GetRequiredService<ILlamaCppSourceBuildEventPublisher>(),
                sp.GetRequiredService<ILogger<LlamaCppSourceBuildService>>()));
        services.TryAddSingleton<ICudaBuildService, LegacyCudaBuildServiceAdapter>();
        services.AddHostedService(static sp => new CudaBuildStartupService(sp.GetRequiredService<ILlamaCppSourceBuildService>(),
            sp.GetRequiredService<IInstalledRuntimeStore>(),
            sp.GetRequiredService<ICudaManagedBuildSignal>(),
            sp.GetRequiredService<ILogger<CudaBuildStartupService>>()));

        // Local cross-encoder reranker: spawns/reuses a rerank-role llama-server (--rerank + --pooling rank) for the
        // resolved reranker model and POSTs /v1/rerank. Uses the caller-supplied HttpClient (AddHttpClient) — the same
        // plain-client seam the health probe uses — since /v1/rerank has no OpenAI-SDK method. Singleton (stateless); the
        // supervisor owns the underlying process. Any failure degrades to null so knowledge search keeps its fusion order.
        services.TryAddSingleton<IRerankerClient>(static sp =>
            new LlamaServerRerankerClient(sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<ILogger<LlamaServerRerankerClient>>()));

        // SEAM: the llamacpp ILocalModelProvider. Registered over the supervisor + the caller-supplied
        // IGgufModelStore (the Hugging Face GGUF store). Added to the
        // ILocalModelProvider set alongside Ollama; the per-model→provider resolver dispatches across both
        // registrations. Singleton — it holds no per-request state; the deferred chat/embedding
        // clients it hands out own the cold-start.
        services.TryAddSingleton<LlamaServerLocalModelProvider>(static sp =>
            new LlamaServerLocalModelProvider(sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<IGgufModelStore>(),
                sp.GetRequiredService<LlamaServerSupervisorOptions>(),
                sp.GetRequiredService<ITokenEstimatorCalibrationScheduler>(),
                sp.GetRequiredService<ILlamaServerEndpointBinding>()));
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
