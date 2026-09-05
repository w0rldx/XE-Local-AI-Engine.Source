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
using XE_Local_AI_Engine.Providers.HuggingFace.Telemetry;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

internal static class AddNodeModelFitExtensions
{
    public static IHostApplicationBuilder AddNodeModelFit(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Model-fit persistence stores. Snapshots carry sanitized-by-default summaries; the encrypted raw output, stderr
        // and diagnostics are exposed only on the explicit operator-diagnostics read. Recommendation and benchmark rows
        // are normalized snapshot projections. Scoped to match the scoped, DbContext-backed stores.
        // The approved-image registry store is no longer registered — the approved-image concept (its query-
        // service read + the list endpoint) was removed. The orphaned table/entity is left in place (no
        // destructive migration); nothing writes or reads it.
        builder.Services.AddScoped<IModelFitSnapshotStore, ModelFitSnapshotStore>();
        builder.Services.AddScoped<IModelFitRecommendationStore, ModelFitRecommendationStore>();
        builder.Services.AddScoped<IModelFitBenchmarkStore, ModelFitBenchmarkStore>();
        // Inference-profile persistence: one live llama-server launch config per (machine_key, model, role, backend) plus
        // its freeze/stale status transitions. Plaintext structural rows (no encryption interceptor). Scoped to match the
        // scoped, DbContext-backed stores.
        builder.Services.AddScoped<IInferenceProfileStore, InferenceProfileStore>();

        // Inference Optimizer orchestrator: explore → benchmark → freeze over the supervisor's exclusive
        // profiling entry point. The machine-readable fit-output parser and the OpenAI chat-client factory are public seams over the
        // provider-internal parser/adapter so this layer stays Application → Providers. The metadata reader exposes the
        // GGUF MoE/param/quant/context inputs over the internal header reader (AddHuggingFaceGgufStore registered it in
        // AddNodeModelRuntime). The harness is stateless → singleton; the orchestrator composes the Scoped profile +
        // model-fit snapshot/benchmark stores → Scoped.
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

        // Curated model catalog: bundled JSON + optional operator-configured remote refresh.
        // The options section binds ModelCatalog:RefreshUrl/RefreshTtl/FetchTimeout (empty RefreshUrl = bundled-only,
        // never a network call). The named HttpClient is resolved via IHttpClientFactory, never injected as a bare
        // HttpClient — this keeps every FastEndpoints ctor (instantiated at startup) test-factory-safe by construction.
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
        // The GGUF variant recommender annotates a repo's selectable files (quality tier + hardware fit verdict + a single
        // recommended pick) for the download picker's inspect endpoint. Stateless over the singleton GPU-variant selector
        // and free-VRAM probe → singleton. Read-time only; never persists.
        builder.Services.AddSingleton<IGgufVariantRecommender, GgufVariantRecommender>();
        // Model-fit refresh service = the local model advisor: the single non-bypass path that profiles hardware,
        // discovers candidate GGUF files (the Hugging Face GGUF store), estimates memory fit, ranks the survivors and
        // replaces the cached recommendation snapshot. Invoked only by the scheduler's ModelRecommendationCheckHandler.
        // Scoped because it composes the Scoped DbContext-backed snapshot/recommendation stores (the hardware-profiler
        // and GGUF-store seams it depends on are singletons).
        builder.Services.AddScoped<IModelFitRefreshService, ModelFitRefreshService>();
        // The operator-driven GGUF download coordinator owns a per-model cancellation registry so a download
        // started by one HTTP request can be cancelled by a separate request, and tracks the latest sanitized progress.
        // Singleton because the download runs detached after the request scope that started it has returned (it composes
        // the singleton Hugging Face GGUF store IGgufModelStore). The advisor management endpoints (download/cancel)
        // consume it.
        builder.Services.AddSingleton<IGgufAcquisitionOperationRegistry, GgufAcquisitionOperationRegistry>();
        builder.Services.AddSingleton<IGgufDownloadCoordinator, GgufDownloadCoordinator>();
        builder.Services.AddSingleton<IGgufImportTransactionCoordinator, GgufImportTransactionCoordinator>();
        // No-op download event publisher default — the coordinator (singleton) resolves a publisher even in
        // Application-only / test hosts that wire no SignalR hub. The Client host supersedes this with a hub-backed
        // publisher so download status changes push live to operator clients (replacing the per-second downloads poll).
        builder.Services.AddSingleton<IGgufDownloadEventPublisher, NullGgufDownloadEventPublisher>();
        // Model-fit local-API services. The query service is a pure cache reader over the persistence stores (sanitized
        // snapshot summary + normalized recommendation rows) and takes NO dependency on the runner or refresh service, so
        // a read can never start an advisor run. The refresh trigger is a template-guarded facade over
        // the scheduler trigger service: it fires only an existing model-recommendation-check definition and never runs
        // the advisor itself. Both are Scoped because they compose the Scoped, DbContext-backed stores / scheduler service.
        builder.Services.AddScoped<IModelFitQueryService, ModelFitQueryService>();
        builder.Services.AddScoped<IModelFitRefreshTrigger, ModelFitRefreshTrigger>();

        // The provider-neutral hardware profiler reports RAM, VRAM, GPU vendor, CPU, and free disk across platforms.
        // Singleton — the profile is cached in memory and re-probed only on forceRefresh:true.
        // The free-disk figure is reported for the models volume, resolved here from the node data dir (the same root the
        // INodeDataDirectory abstraction resolves: the per-user data dir in desktop mode, ContentRootPath otherwise — the
        // profiler is registered with a plain string at config time, so it reads the NodeData:Directory key directly).
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

        // Report-only llama-server spawn/readiness/placement observations. Registered before the provider module; its
        // TryAdd null default therefore leaves this shared NodeMetrics bridge in place. The concrete type is registered
        // too, and the interface forwards to it, because the same instance also holds the last-successful-load VRAM
        // record the dev-workflow cost collector reads — two registrations of the class would be two caches, one of
        // them never written.
        builder.Services.AddSingleton<NodeMetricsLlamaServerLoadTelemetry>();
        builder.Services.AddSingleton<ILlamaServerLoadTelemetry>(services => services.GetRequiredService<NodeMetricsLlamaServerLoadTelemetry>());

        // Runtime device audit: composes the hardware profiler + the GPU-variant selector + the device-inventory
        // probe to detect a silent CPU fallback (a GPU box whose selected runtime runs on the CPU), and exposes the
        // audited EFFECTIVE hardware profile the advisor + capacity gate size against. Singleton — it memoizes the
        // binary-derived audit and depends only on singletons (the profiler, selector, and device probe). The
        // device-inventory probe (ILlamaDeviceInventoryProbe) is registered by the llama-server provider stack.
        builder.Services.AddSingleton<IRuntimeDeviceAudit, RuntimeDeviceAuditService>();

        return builder;
    }
}
