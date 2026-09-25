namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
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
    ///     Backstop deadline on the rerank client, restoring one that Aspire's standard pipeline takes away (see the
    ///     reranker registration).
    /// </summary>
    /// <remarks>
    ///     It must stay ABOVE <c>LlamaServerRerankerClient.ResolveRequestTimeout</c>'s ceiling, or it fires first and
    ///     the reranker's own degrade-to-fusion-order path never runs.
    /// </remarks>
    private static readonly TimeSpan RerankBackstopTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Registers the model-runtime core — GPU vendor probe, OS-aware variant selector, llama.cpp binary manager —
    ///     plus <see cref="ILlamaServerProcessSupervisor" /> and the <c>llamacpp</c> <c>ILocalModelProvider</c>, wired
    ///     into the multi-provider resolver.
    /// </summary>
    /// <remarks>
    ///     CALLER CONTRACT: the consuming application must register a named/typed
    ///     <see cref="System.Net.Http.HttpClient" /> for binary downloads via <c>AddHttpClient</c> — the
    ///     <c>Microsoft.Extensions.Http</c> package is referenced by the Application host, not this provider project —
    ///     and supply an <see cref="IGgufModelStore" />, the Hugging Face GGUF store (<c>AddHuggingFaceGgufStore</c>).
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

        // Operator bring-your-own llama-server override, operator-trust ONLY: built once from process env vars via explicit reads, NEVER from an IConfiguration
        // section, the user-editable node settings store or a request DTO. Off by default; ONE instance is shared by the selector and the binary manager.
        var overrideOptions = LlamaServerRuntimeOverrideOptions.FromEnvironment();
        services.TryAddSingleton(overrideOptions);

        // The selector takes the override options as a dependency, so register it via an explicit factory (rather than the
        // type-based registration) so the new dependency resolves deterministically.
        services.TryAddSingleton<IGpuVariantSelector>(static sp =>
            new GpuVariantSelector(sp.GetRequiredService<IGpuVendorProbe>(),
                sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<ICudaManagedBuildSignal>(),
                sp.GetRequiredService<IInstalledRuntimeStore>()));

        // Dynamic-runtime resolution seams: the live GitHub Releases catalog (tier 1) and the on-disk installed-runtime state (tier 2), with the pinned floor
        // behind them. These two ALONE keep the host's shared default factory client — a deliberate KEEP: each issues an idempotent GET, so a retry costs a read, never a side effect.
        services.TryAddSingleton<ILlamaCppReleaseCatalog>(static sp =>
            new GitHubLlamaCppReleaseCatalog(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IInstalledRuntimeStore>(static _ => new InstalledRuntimeStore());

        // Shared "is there a newer runtime?" snapshot — written once by the startup check service and after a successful
        // update install, read by the read-only runtime-status endpoint. Decoupled from any app-package updater channel.
        services.TryAddSingleton<ILlamaCppUpdateState, LlamaCppUpdateState>();

        // First-run acquisition visibility: the no-op publisher keeps provider-only, headless and CI hosts silent, and the Client host swaps in a hub-backed one.
        // The manager depends on the REGISTRY, never the publisher: routing every write through what stamps the sequence makes "recorded but never broadcast" unrepresentable.
        services.TryAddSingleton<IRuntimeAcquisitionEventPublisher, NullRuntimeAcquisitionEventPublisher>();
        services.TryAddSingleton<IRuntimeAcquisitionStatusRegistry, RuntimeAcquisitionStatusRegistry>();

        services.TryAddSingleton<ILlamaCppBinaryManager>(static sp =>
            new LlamaCppBinaryManager(sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<TimeProvider>(),
                cacheRoot: null,
                activeTag: null,
                sp.GetRequiredService<ILlamaCppReleaseCatalog>(),
                sp.GetRequiredService<IInstalledRuntimeStore>(),
                sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
                sp.GetRequiredService<ICudaManagedBuildSignal>(),
                sp.GetRequiredService<IRuntimeAcquisitionStatusRegistry>()));

        // In-app Linux source build (no upstream prebuilt CUDA asset exists): the per-backend prerequisite probe, the no-op build-event publisher (the Client host
        // swaps in a hub-backed one) and the single-flight build service; the startup service cleans a stale work dir and seeds the managed-CUDA signal from the record.
        services.TryAddSingleton<ILlamaCppSourceBuildPrerequisiteProbe>(static sp =>
            new LlamaCppSourceBuildPrerequisiteProbe(sp.GetRequiredService<IGpuVendorProbe>()));
        services.TryAddSingleton<ILlamaCppSourceBuildEventPublisher, NullLlamaCppSourceBuildEventPublisher>();
        services.TryAddSingleton<ILlamaCppSourceBuildActivity, LlamaCppSourceBuildActivity>();

        // Conversion tooling for training exports. Provisioned lazily on the first export, never at startup — a node
        // that never trains never fetches it.
        services.TryAddSingleton<IConvertScriptSourceFetcher, GitConvertScriptSourceFetcher>();
        services.TryAddSingleton<IConvertScriptProvisioner, ConvertScriptProvisioner>();

        // Real llama.cpp process-VRAM-budget probe, parsing `llama-server --list-devices`. PLAIN AddSingleton, not TryAdd, so it WINS over the Application layer's
        // UnknownProcessVramBudgetProbe floor regardless of registration order: TryAdd no-ops once a registration exists, and last-wins resolves to this one.
        services.AddSingleton<IProcessVramBudgetProbe, LlamaListDevicesProcessVramBudgetProbe>();

        // Device-inventory probe: parses `llama-server --list-devices` into a structured variant-plus-devices reading, sharing the process runner with the VRAM
        // probe and cached per resolved binary. The Application-layer runtime device audit uses it to spot a GPU-variant binary enumerating zero devices — a silent CPU fallback.
        services.TryAddSingleton<ILlamaDeviceInventoryProbe, LlamaDeviceInventoryProbe>();

        // Probe the resolved executable rather than infer flags from a tag. The successful --version/--help result is cached per
        // requested-version/path/length/mtime/SHA-256 identity and gates every final launch vector, including BYO and source builds.
        services.TryAddSingleton<ILlamaServerCapabilityManifestProbe, LlamaServerCapabilityManifestProbe>();

        // The public question-answering seam over that same probe, for callers outside this provider that must settle a launch vector BEFORE a spawn exists
        // (the benchmark freeze). It exposes neither the manifest nor the resolved binary, so no path crosses the boundary.
        services.TryAddSingleton<ILlamaServerLaunchCapabilityInspector>(static sp =>
            new LlamaServerLaunchCapabilityInspector(sp.GetRequiredService<IGpuVariantSelector>(),
                sp.GetRequiredService<ILlamaCppBinaryManager>(),
                sp.GetRequiredService<ILlamaServerCapabilityManifestProbe>()));

        // GPU-load admission floor: a no-op serializer so a provider-only host resolves the gate even when the application layer registered no real, metric-emitting
        // one. The composition root overrides it with a plain AddSingleton (last-wins), so the LLM and image supervisors share ONE process-wide gate.
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

        // DEDICATED HttpClient bypassing the app's IHttpClientFactory, so the readiness probe never inherits the standard resilience handler's exponential retries,
        // which detect readiness seconds late. Localhost-only, so handler rotation is unnecessary; the backstop Timeout sits above the probe's own bounds. Never disposed, by design.
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

        // Self-satisfying launch-arg resolver: explore-mode (auto-fit) until the Application host registers its DB-backed IInferenceProfileResolver last (last
        // registration wins), which keeps the layer arrow Application → Providers — the interface is DEFINED here and implemented in Application.
        services.TryAddSingleton<IInferenceProfileResolver, DefaultInferenceProfileResolver>();

        // Self-satisfying per-model extra-launch-arg resolver: empty (no override) until the Application host registers its store-backed resolver last (last
        // registration wins), which keeps the layer arrow Application → Providers — the interface is DEFINED here and implemented in Application.
        services.TryAddSingleton<ILlamaServerExtraLaunchArgumentsResolver, EmptyLlamaServerExtraLaunchArgumentsResolver>();

        // Measured GPU layer placement for the node: the supervisor writes it as models load and the runtime device audit reads it for the operator UI. Both must
        // see the SAME instance, so it is registered before the supervisor and passed in explicitly rather than left to the supervisor's private default.
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
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<LlamaServerExternalEndpointOptions>(),
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
                sp.GetRequiredService<ILogger<LlamaCppSourceBuildService>>(),
                sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(static sp => new CudaBuildStartupService(sp.GetRequiredService<ILlamaCppSourceBuildService>(),
            sp.GetRequiredService<IInstalledRuntimeStore>(),
            sp.GetRequiredService<ICudaManagedBuildSignal>(),
            sp.GetRequiredService<ILogger<CudaBuildStartupService>>()));

        // Local cross-encoder reranker: a stateless singleton spawning or reusing a rerank-role llama-server and POSTing /v1/rerank; the supervisor owns the process, and a
        // failure degrades to null so search keeps its fusion order. Its OWN resilience-free client and finite timeout: docs/wiki/03-local-runtime-and-providers.md, "DI wiring".
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; used deliberately to drop the
        // Aspire-installed standard pipeline, whose blanket retries duplicate GPU
        // compute on a non-idempotent rerank POST.
        services.AddHttpClient(LlamaServerRerankerClient.HttpClientName,
                    static client => client.Timeout = RerankBackstopTimeout)
                .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        services.TryAddSingleton<IRerankerClient>(static sp =>
            new LlamaServerRerankerClient(sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(LlamaServerRerankerClient.HttpClientName),
                sp.GetRequiredService<ILogger<LlamaServerRerankerClient>>()));

        // SEAM: the llamacpp ILocalModelProvider, over the supervisor and the caller-supplied IGgufModelStore (the Hugging Face GGUF store), added to the
        // ILocalModelProvider set alongside Ollama so the per-model resolver dispatches across both. Singleton — it holds no per-request state, and the deferred clients own the cold-start.
        services.TryAddSingleton<LlamaServerLocalModelProvider>(static sp =>
            new LlamaServerLocalModelProvider(sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<IGgufModelStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<LlamaServerSupervisorOptions>(),
                sp.GetRequiredService<ITokenEstimatorCalibrationScheduler>(),
                sp.GetRequiredService<ILlamaServerEndpointBinding>()));
        services.AddSingleton<ILocalModelProvider>(static sp =>
            sp.GetRequiredService<LlamaServerLocalModelProvider>());

        // Startup orphan reaper: a hard host kill skips the supervisor's graceful DisposeAsync teardown, orphaning a server that still holds its loopback port and VRAM and blocks the next start.
        // It matches ONLY binaries under our own llama.cpp cache root, so an unrelated llama-server is never touched, and never throws out of StartAsync, so it cannot block startup.
        services.TryAddSingleton<IStaleLlamaServerProcessScanner, OsStaleLlamaServerProcessScanner>();
        services.AddHostedService(static sp => new StaleLlamaServerReaper(sp.GetRequiredService<IStaleLlamaServerProcessScanner>(),
            LlamaCppBinaryManager.DefaultLlamaCppBinariesRoot(),
            sp.GetRequiredService<ILogger<StaleLlamaServerReaper>>()));

        // Startup notice: an active bring-your-own override is logged once at Warning, so it is obvious that an unverified operator-supplied binary is in use and
        // integrity hash verification is skipped. Nothing is logged when the override is unset, so a normal deploy is byte-behavior-unchanged.
        services.AddHostedService(static sp => new LlamaServerRuntimeOverrideStartupNotice(sp.GetRequiredService<LlamaServerRuntimeOverrideOptions>(),
            sp.GetRequiredService<ILogger<LlamaServerRuntimeOverrideStartupNotice>>()));

        return services;
    }
}
