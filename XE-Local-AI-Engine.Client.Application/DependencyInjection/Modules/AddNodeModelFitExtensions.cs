namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Capabilities;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

internal static class AddNodeModelFitExtensions
{
    /// <summary>
    ///     Registers the model-fit stack: its stores, the inference optimizer, the curated model catalog, the GGUF
    ///     download coordinators, the hardware profiler and the runtime device audit.
    /// </summary>
    /// <remarks>
    ///     No approved-image registry store is registered: the approved-image concept is gone, and its table and entity
    ///     stay in place unread, since no destructive migration removes them.
    /// </remarks>
    public static IHostApplicationBuilder AddNodeModelFit(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Model-fit persistence stores, Scoped to match the DbContext-backed stores: snapshot summaries are sanitized by default, the
        // encrypted raw output, stderr and diagnostics needing the operator-diagnostics read, and recommendation and benchmark rows are projections.
        builder.Services.AddScoped<IModelFitSnapshotStore, ModelFitSnapshotStore>();
        builder.Services.AddScoped<IModelFitRecommendationStore, ModelFitRecommendationStore>();
        builder.Services.AddScoped<IModelFitBenchmarkStore, ModelFitBenchmarkStore>();
        // Inference-profile persistence: one live llama-server launch config per (machine_key, model, role, backend) plus its
        // freeze/stale status transitions. Plaintext structural rows, no encryption interceptor. Scoped like the DbContext-backed stores.
        builder.Services.AddScoped<IInferenceProfileStore, InferenceProfileStore>();

        // Inference Optimizer orchestrator: explore → benchmark → freeze over the supervisor's exclusive profiling entry point. The
        // fit-output parser, chat-client factory and GGUF metadata reader are public seams over provider internals, keeping this layer Application → Providers.
        builder.Services.AddGgufMetadataReader();
        builder.Services.AddSingleton<IFittedArgsParser, FittedArgsParser>();
        builder.Services.AddSingleton<IInferenceChatClientFactory, OpenAiInferenceChatClientFactory>();
        builder.Services.Configure<InferenceBenchmarkVramAdmissionOptions>(configuration.GetSection(InferenceBenchmarkVramAdmissionOptions.SectionName));
        builder.Services.AddSingleton<IInferenceBenchmarkHarness, InferenceBenchmarkHarness>();
        // One cache, one set of file-system watchers: the fingerprint provider and the benchmark environment capture
        // hash the same runtime directory and would otherwise watch it twice.
        builder.Services.AddSingleton<LaunchPolicyFileHashCache>();
        builder.Services.AddSingleton<ILaunchPolicyFingerprintProvider, LaunchPolicyFingerprintProvider>();
        builder.Services.AddScoped<IInferenceProfileService, InferenceProfileService>();
        // The request validator allowlists the recommend intent params (use-case + limit bounds). Stateless → singleton.
        builder.Services.AddSingleton<ModelFitRequestValidator>();

        // Curated model catalog: bundled JSON plus an optional operator-configured remote refresh, bound from ModelCatalog:RefreshUrl,
        // RefreshTtl and FetchTimeout (an empty RefreshUrl is bundled-only, never a network call). A named client, so startup-built endpoint ctors stay test-safe.
        builder.Services.Configure<ModelCatalogOptions>(configuration.GetSection(ModelCatalogOptions.SectionName));
        // The catalog document is a few KB; cap the response buffer well above that (5 MB) so a misconfigured or
        // compromised RefreshUrl can never make the node buffer an unbounded response body in memory.
        builder.Services.AddHttpClient(ModelCatalogOptions.HttpClientName)
               .ConfigureHttpClient(static client => client.MaxResponseContentBufferSize = 5 * 1024 * 1024);
        // The cache store persists a tiny node-local JSON file, mirroring NodeSettingsStore. The provider owns the
        // in-memory bundled/remote/last-good snapshot plus TTL-gated refresh serialization. Both singletons.
        builder.Services.AddSingleton<IModelCatalogCacheStore, ModelCatalogCacheStore>();
        builder.Services.AddSingleton<IModelCatalogProvider, ModelCatalogProvider>();
        // The catalog ranking lane composes only singleton seams (catalog provider, HF discovery, estimator, llama.cpp
        // update state) → singleton.
        builder.Services.AddSingleton<ICatalogRecommendationService, CatalogRecommendationService>();
        // The memory-fit estimator is a pure, stateless function over GGUF header metadata + the hardware
        // profile → singleton. Consumed by the advisor to score each candidate GGUF file's fit.
        builder.Services.AddSingleton<MemoryFitEstimator>();
        // The GGUF variant recommender annotates a repo's selectable files (quality tier, hardware fit verdict, one recommended pick)
        // for the download picker's inspect endpoint. Singleton: stateless over the GPU-variant selector and free-VRAM probe, and never persists.
        builder.Services.AddSingleton<IGgufVariantRecommender, GgufVariantRecommender>();
        // The local model advisor: the single non-bypass path that profiles hardware, discovers candidate GGUF files, estimates memory
        // fit, ranks survivors and replaces the cached snapshot. Only ModelRecommendationCheckHandler invokes it. Scoped, over the scoped stores.
        builder.Services.AddScoped<IModelFitRefreshService, ModelFitRefreshService>();
        // The operator-driven GGUF download coordinator owns a per-model cancellation registry, so a download started by one HTTP
        // request is cancellable by another, and tracks sanitized progress. Singleton: the download runs detached after its request scope returns.
        builder.Services.AddSingleton<IGgufAcquisitionOperationRegistry, GgufAcquisitionOperationRegistry>();
        builder.Services.AddSingleton<IGgufDownloadCoordinator, GgufDownloadCoordinator>();
        builder.Services.AddSingleton<IGgufImportTransactionCoordinator, GgufImportTransactionCoordinator>();
        // No-op download event publisher default, so the singleton coordinator resolves a publisher in Application-only and test hosts
        // that wire no hub. The Client host supersedes it with a hub-backed publisher pushing download status changes live.
        builder.Services.AddSingleton<IGgufDownloadEventPublisher, NullGgufDownloadEventPublisher>();
        // Model-fit local-API services. The query service is a pure cache reader over the stores, with NO dependency on the runner or
        // refresh service, so a read never starts an advisor run; the refresh trigger only fires an existing definition. Both Scoped.
        builder.Services.AddScoped<IModelFitQueryService, ModelFitQueryService>();
        builder.Services.AddScoped<IModelFitRefreshTrigger, ModelFitRefreshTrigger>();

        // The provider-neutral hardware profiler reports RAM, VRAM, GPU vendor, CPU and free disk across platforms. Singleton: the
        // profile is cached and re-probed only on forceRefresh. Free disk is the models volume, from the same root INodeDataDirectory resolves.
        var dataDirectoryRoot = configuration[NodeDataDirectory.ConfigurationKey];
        builder.Services.AddHardwareProfiler(string.IsNullOrWhiteSpace(dataDirectoryRoot)
            ? builder.Environment.ContentRootPath
            : dataDirectoryRoot);

        // Bridge the profiler's probe-timeout metrics seam to the application NodeMetrics meter (the Capabilities layer
        // cannot reference it directly). Registered after AddHardwareProfiler so it overrides the null default.
        builder.Services.AddSingleton<IHardwareProbeMetrics, NodeMetricsHardwareProbeMetrics>();

        // Same bridge for the HF download read-idle-timeout seam (Providers.HuggingFace cannot reference the
        // application meter). A plain registration wins over the null default the HF store module registers.
        builder.Services.AddSingleton<IHfDownloadMetrics, NodeMetricsHfDownloadMetrics>();

        // Report-only llama-server spawn/readiness/placement observations, registered BEFORE the provider module so its TryAdd null
        // default leaves this NodeMetrics bridge in place. Concrete plus forwarding interface: one instance also holds the VRAM record the cost collector reads.
        builder.Services.AddSingleton<NodeMetricsLlamaServerLoadTelemetry>();
        builder.Services.AddSingleton<ILlamaServerLoadTelemetry>(services => services.GetRequiredService<NodeMetricsLlamaServerLoadTelemetry>());

        // Runtime device audit: composes the hardware profiler, the GPU-variant selector and ILlamaDeviceInventoryProbe (from the
        // llama-server provider) to catch a silent CPU fallback and expose the EFFECTIVE profile the advisor and capacity gate size against.
        builder.Services.AddSingleton<IRuntimeDeviceAudit, RuntimeDeviceAuditService>();

        // Beats the DefaultCudaDeviceProbe that the whisper and sd-server modules TryAdd (they run later, so their TryAdd is a no-op): the
        // cached audit already knows when the CUDA driver enumerates no device, which the default's driver-library check cannot see.
        builder.Services.AddSingleton<ICudaDeviceProbe, RuntimeAuditCudaDeviceProbe>();

        return builder;
    }
}
