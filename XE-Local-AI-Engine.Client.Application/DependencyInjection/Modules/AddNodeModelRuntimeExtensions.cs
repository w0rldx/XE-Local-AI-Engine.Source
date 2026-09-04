namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.HuggingFace;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Providers.OpenAICompat;

internal static class AddNodeModelRuntimeExtensions
{
    private const string UseLocalModelProviderConfigurationKey = "XE_USE_LOCAL_MODEL_PROVIDER";

    // Capability gate for the optional Ollama runtime. Enabled when unset, so the default registration is unchanged. The
    // key lives on OllamaRuntimeGate so the running-models endpoint reads the SAME gate (no drift).
    private const string OllamaRuntimeEnabledConfigurationKey = OllamaRuntimeGate.RuntimeEnabledConfigurationKey;

    // Opt-in escape hatch for a non-loopback Ollama endpoint. Off by default — the local Ollama API is unauthenticated.
    private const string OllamaAllowRemoteEndpointConfigurationKey = "XE_OLLAMA_ALLOW_REMOTE_ENDPOINT";

    public static IHostApplicationBuilder AddNodeModelRuntime(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton<WorkerHubConnection>(sp =>
        {
            var connection = ActivatorUtilities.CreateInstance<WorkerHubConnection>(sp);
            var dispatcher = new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>());
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("WorkerHubConnectionEventBindings");

            connection.InvocationAssignedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchInvocationAssignedV2Async(args.Envelope), logger, nameof(IWorkerEventDispatcher.DispatchInvocationAssignedV2Async));
            connection.ToolCallResultReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchToolCallResultAsync(args.ToolCallResult), logger, nameof(IWorkerEventDispatcher.DispatchToolCallResultAsync));
            connection.DisconnectRequestedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchDisconnectRequestedAsync(args.DisconnectRequest), logger, nameof(IWorkerEventDispatcher.DispatchDisconnectRequestedAsync));
            connection.ApprovalResolvedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchApprovalResolvedAsync(args.ApprovalResolution), logger, nameof(IWorkerEventDispatcher.DispatchApprovalResolvedAsync));
            connection.InvocationCancelledReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchInvocationCancelledAsync(args.Cancellation), logger, nameof(IWorkerEventDispatcher.DispatchInvocationCancelledAsync));

            return connection;
        });
        builder.Services.AddSingleton<IWorkerHubConnection>(sp => sp.GetRequiredService<WorkerHubConnection>());
        builder.Services.AddSingleton<IHubMessageSender>(sp => sp.GetRequiredService<WorkerHubConnection>());
        builder.Services.AddSingleton<ICertPinStore, CertPinStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<NodeChatMigrationRecoveryService>();
        builder.Services.AddSingleton<INodeDbBackupService, NodeDbBackupService>();
        builder.Services.AddSingleton<IKnowledgeDowngradeSafetyService, KnowledgeDowngradeSafetyService>();

        // Node SQLite concurrency posture. Resolve the connection-time pragma settings once and (a) publish them
        // to the static raw-open helpers (NodeSqlitePragmas.Configure — the raw-ADO OpenIfNeeded path cannot take injected
        // options) and (b) register the interceptors that apply the pragmas on EF-initiated opens and account contention.
        var sqlitePragmaSettings = (configuration.GetSection(NodeSqliteOptions.Section).Get<NodeSqliteOptions>() ?? new NodeSqliteOptions()).ToSettings();
        NodeSqlitePragmas.Configure(sqlitePragmaSettings);
        builder.Services.AddSingleton(sqlitePragmaSettings);
        builder.Services.AddSingleton<NodeSqliteConnectionInterceptor>();
        builder.Services.AddSingleton<NodeSqliteCommandInterceptor>();

        builder.Services.AddDbContext<NodeChatDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString)
                   .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                   // EF's STATIC ServiceProviderCache keys on the options (incl. this connection string) and each entry
                   // strongly roots the whole application ServiceProvider. The product builds one host with one
                   // connection string, so the default caching is a single entry and stays on. Test hosts use a fresh
                   // per-host SQLite path, so every host would add a new immortal entry — the fixtures set this flag
                   // to false, which bypasses the static cache entirely (docs/agent-knowledge.md §1).
                   .EnableServiceProviderCaching(configuration.GetValue("EntityFramework:ServiceProviderCaching", defaultValue: true))
                   .AddInterceptors(serviceProvider.GetRequiredService<NodeSqliteConnectionInterceptor>(),
                       serviceProvider.GetRequiredService<NodeSqliteCommandInterceptor>(),
                       serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
        });

        builder.Services.AddDbContext<NodeIdentityDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString,
                       sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable))
                   // Same static-cache rooting consideration as the NodeChatDbContext registration above.
                   .EnableServiceProviderCaching(configuration.GetValue("EntityFramework:ServiceProviderCaching", defaultValue: true))
                   .AddInterceptors(serviceProvider.GetRequiredService<NodeSqliteConnectionInterceptor>(),
                       serviceProvider.GetRequiredService<NodeSqliteCommandInterceptor>());
        });

        // Embeddings are provider-routed: EmbeddingPlaybookRetrievalRanker resolves the embedding provider
        // by PlaybookRetrievalOptions.EmbeddingProviderName via ILocalModelProviderResolver and builds/owns its own
        // generator per send (node-local; ollama or llamacpp). There is intentionally no standalone DI-registered
        // IEmbeddingGenerator — the previous Ollama hardwire and its only consumer (the unused LocalEmbeddingService
        // adapter) were removed so nothing contradicts the multi-provider design.

        // Ollama is an OPTIONAL secondary local runtime (Decision #1: keep + isolate). ALL Ollama-specific wiring lives
        // in AddOllamaRuntime, so this module is the single seam that references Providers.Ollama — runtime selection has
        // one capability gate, never a second provider-direct code path. Enabled by default, so the registration is
        // byte-identical to the previous inline call unless an operator opts out.
        AddOllamaRuntime(builder, configuration);

        // Register the llama-server provider stack ALONGSIDE Ollama so the resolver can
        // dispatch a model to either runtime. AddLlamaServerLocalModelProvider adds the binary manager, the GPU
        // variant probe, the process supervisor, and the "llamacpp" ILocalModelProvider into the provider set.
        // Caller-contract dependencies (the provider project intentionally takes them from the host):
        //   • an HttpClient for binary downloads + health probes (AddHttpClient),
        //   • an IGgufModelStore — the Hugging Face GGUF store, registered just below by AddHuggingFaceGgufStore.
        // AddHuggingFaceGgufStore provides the real IGgufModelStore (HF discovery + download + disk
        // guard + registry); the optional HF token rides the encrypted HfTokenStore (third IDataProtector .enc store).
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IHfTokenStore, HfTokenStore>();

        // Dependency direction: the Providers.* projects reference ONLY Providers.Abstractions and must NOT depend on
        // Client.Application (where INodeRuntimeSettings lives). The provider option objects are therefore SEEDED from
        // the accessor here, at the composition root (Client.Application legitimately references both). Both options are
        // read at host build, so an operator edit applies on the next process restart. Each seeded instance
        // is registered BEFORE the provider extension so its own TryAddSingleton default becomes a no-op (no
        // double-registration). The one-time blocking accessor read runs once at singleton construction — not a hot path.
        builder.Services.AddSingleton(sp => BuildSeededHuggingFaceOptions(sp, configuration));
        builder.Services.AddHuggingFaceGgufStore(configuration);

        builder.Services.AddSingleton(sp => BuildSeededLlamaServerSupervisorOptions(sp));
        builder.Services.AddSingleton(sp => BuildSeededLlamaServerLaunchPolicyOptions(sp));
        builder.Services.AddLlamaServerLocalModelProvider();
        builder.Services.AddSingleton<ILlamaCppRuntimeAdministrationService, LlamaCppRuntimeAdministrationService>();

        // The process-wide GPU-load admission gate — the REAL, metric-emitting singleton shared by the
        // llama-server and stable-diffusion.cpp supervisors, so no two GPU loads race their --fit / free-VRAM reads. A
        // plain AddSingleton wins over each provider's TryAddSingleton<IGpuModelLoadAdmission, NoOpGpuModelLoadAdmission>()
        // floor (last registration wins). The bounded max-wait is a backstop the size-aware readiness timeouts already
        // make rare; captured at host build, applied on the next process restart.
        builder.Services.AddSingleton(new GpuModelLoadAdmissionOptions());
        builder.Services.AddSingleton<IGpuModelLoadAdmission, GpuModelLoadAdmission>();

        // Inference Optimizer: profile-driven launch-arg replay. Registered AFTER AddLlamaServerLocalModelProvider so
        // the real DB-backed resolver OVERRIDES the provider's explore-only DefaultInferenceProfileResolver — a plain
        // AddSingleton beats the provider's TryAddSingleton (last registration wins), keeping the layer arrow
        // Application → Providers (the interface is defined in Providers, implemented here). The resolver is a singleton
        // on the cold spawn path; it opens a fresh scope per resolve to reach the SCOPED IInferenceProfileStore.
        // IMachineKeyProvider + IInferenceInvalidationEvaluator are singletons it injects. This registers the
        // UnknownProcessVramBudgetProbe as a TryAddSingleton fallback only: the real --list-devices probe has shipped
        // (LlamaListDevicesProcessVramBudgetProbe, registered by the LlamaServer provider) and overrides this floor over the same
        // seam, so the invalidation evaluator's live-VRAM check runs on supported backends and only this fallback skips.
        builder.Services.AddSingleton<IMachineKeyProvider, MachineKeyProvider>();
        builder.Services.TryAddSingleton<IProcessVramBudgetProbe, UnknownProcessVramBudgetProbe>();
        builder.Services.AddSingleton<IInferenceInvalidationEvaluator, InferenceInvalidationEvaluator>();
        builder.Services.AddSingleton<IInferenceProfileResolver, InferenceProfileResolver>();

        // Per-model developer/advanced extra-launch-arg override, read on the cold spawn path. Registered last so it wins
        // over the provider's empty default; singleton that reads the scoped override store through a fresh scope per call.
        builder.Services.AddSingleton<ILlamaServerExtraLaunchArgumentsResolver, LlamaServerExtraLaunchArgumentsResolver>();

        // The provider resolver maps ModelName→ProviderName (over the persisted model_provider_map,
        // unmapped → default) then ProviderName→ILocalModelProvider (over the registered set). Singleton; reads the
        // scoped map store through a fresh scope per lookup. DEFAULT for unmapped models = "llamacpp" — Ollama
        // is an OPTIONAL secondary runtime and the shipped default model is a GGUF, so a name that somehow lacks a map
        // row (a pre-existing GGUF install, or a registry/map divergence) still routes to llama.cpp. Genuine Ollama
        // models are explicitly mapped to "ollama" at pull time (the symmetric upsert on the Ollama pull endpoints) going
        // FORWARD, and any model pulled on an EARLIER build (before that upsert existed) is repaired once at startup by
        // OllamaProviderMapBackfill, so the flipped default only ever governs truly-unmapped names — which on a fresh box
        // are GGUFs. The resolver ctor
        // validates the default is registered; llamacpp is always registered above, so the flip cannot throw. The
        // supervisor's loaded-cap is surfaced for the preview reject-at-start check.
        builder.Services.AddSingleton<ILocalModelProviderResolver>(sp =>
        {
            var supervisorOptions = sp.GetRequiredService<LlamaServerSupervisorOptions>();
            return new LocalModelProviderResolver(sp.GetServices<ILocalModelProvider>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                LlamaServerProviderConstants.ProviderName,
                supervisorOptions.MaxLoadedProcesses);
        });

        // The local-branch router is registered as its own singleton so its (provider, model) chat-client cache can be
        // invalidated out-of-band (the runtime-update endpoint clears it after switching the llama.cpp variant, otherwise
        // a cached deferred client keeps pointing at the now-gone endpoint and the next send connection-times-out). The
        // same instance backs both the IChatClient local branch below and ILocalChatClientCacheInvalidator, so clearing
        // the cache and serving sends operate on one cache. Disposal is idempotent, so the container disposing this
        // singleton and RuntimeChatClient disposing its local branch is safe.
        builder.Services.AddSingleton(sp => CreateLocalChatClient(sp, configuration));
        builder.Services.AddSingleton<ILocalChatClientCacheInvalidator>(sp => sp.GetRequiredService<ModelRoutingLocalChatClient>());
        builder.Services.TryAddSingleton<ICloudEgressAuthorizer, DenyDevelopmentCloudEgressAuthorizer>();

        // Register a runtime-re-selecting IChatClient rather than capturing the
        // cloud-vs-local choice once at startup. The wrapper re-evaluates the active provider per send via
        // IActiveCloudChatClientFactory, so signing in/out at runtime takes effect without a node restart.
        // The local branch is the ModelRoutingLocalChatClient — it routes per-send by
        // ChatOptions.ModelId across providers/processes rather than a single fixed-model client.
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var activeCloudFactory = sp.GetRequiredService<IActiveCloudChatClientFactory>();
            return new RuntimeChatClient(activeCloudFactory,
                sp.GetRequiredService<ModelRoutingLocalChatClient>,
                sp.GetRequiredService<ICloudEgressAuthorizer>(),
                // The local branch can now egress: an ext: id routes there by design, so the Development authorization
                // that previously lived only on the cloud branch needs a backstop on this one too.
                sp.GetRequiredService<IModelTrustResolver>());
        });

        builder.Services.AddLocalAiAgentRuntime(builder.Configuration);

        // The node-configured, TIGHTEN-ONLY tool-approval policy. A plain AddSingleton so it wins over the
        // AI.Agent PermissiveToolApprovalPolicy floor (registered via TryAddSingleton inside AddLocalAiAgentRuntime
        // above; last registration wins). The node-default policy is JSON in node settings, read ONCE synchronously at
        // singleton construction (the sync INodeSettingsStore.Load twin, like the tool-capable allow-list seed) so the
        // hot resolve path stays a dictionary lookup; an operator edit applies on the next node restart.
        builder.Services.AddSingleton<IToolApprovalPolicy>(sp =>
            NodeToolApprovalPolicy.FromSettings(sp.GetRequiredService<INodeSettingsStore>().Load()?.ToolApprovalPolicy));

        // The usage-summary cost resolver. Scoped (NOT singleton, unlike the approval policy above) so each
        // usage-summary read reflects the CURRENT operator rate override — the cached node-settings store makes Load() a
        // sub-millisecond in-memory hit, so per-request construction is cheap and rate edits apply without a node restart.
        builder.Services.AddScoped<IUsageRateResolver>(sp =>
            UsageRateResolver.FromSettings(sp.GetRequiredService<INodeSettingsStore>().Load()?.UsageRates));

        // OrchestrationAgentOptions lives in AI.Agent (no reference to Client.Application), so OrchestrationAgentFactory
        // cannot inject INodeRuntimeSettings. Seed the migrated IdleTimeoutSeconds from the accessor here at the
        // composition root via a DI-resolved Configure action — it is appended after the AI.Agent Bind, so a stored
        // value overrides the appsettings seed. The factory caches options.Value at construction (operator edits apply
        // on the next process restart); the blocking accessor read runs once during options materialization, not on any hot path. The
        // accessor is resolved from the real container (no second ServiceProvider build).
        builder.Services.AddOptions<OrchestrationAgentOptions>()
               .Configure<INodeRuntimeSettings>((options, runtimeSettings) =>
                   options.IdleTimeoutSeconds = runtimeSettings.GetOrchestrationIdleTimeoutSeconds());

        AddExternalOpenAiRuntime(builder);

        return builder;
    }

    /// <summary>
    ///     Registers the external OpenAI-compatible multiplexer provider, but ONLY when a real
    ///     <see cref="IExternalProviderRegistry" /> is already in the container.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The guard exists because the provider has no meaningful behavior without the registry that holds the
    ///         operator's connections, and registering an empty stand-in in production would be worse than not
    ///         registering at all: the resolver would happily route an <c>ext:</c> id to a provider that reports zero
    ///         models, which is indistinguishable from "my connections were silently dropped".
    ///     </para>
    ///     <para>
    ///         Because this is a registration-TIME decision it reads the service collection as built so far, so it runs
    ///         last in this module — any composition root adding the encrypted external-provider store must do so before
    ///         <c>AddNodeModelRuntime</c> returns. The provider resolver is unaffected by ordering: it enumerates
    ///         <see cref="ILocalModelProvider" /> at resolution time, not registration time.
    ///     </para>
    /// </remarks>
    private static void AddExternalOpenAiRuntime(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IExternalProviderRegistry)))
        {
            _ = builder.Services.AddExternalOpenAiModelProvider();
        }
    }

    /// <summary>
    ///     Registers the OPTIONAL Ollama local-model runtime as one cohesive, capability-gated block (Decision #1:
    ///     keep + isolate). This is the only place that references <c>Providers.Ollama</c>, so the resolver dispatches a
    ///     model to either this provider or llama.cpp through a single seam. The runtime is enabled unless
    ///     <c>XE_OLLAMA_RUNTIME_ENABLED=false</c>, so the default registration is byte-identical to the previous inline
    ///     call. The resolved endpoint is loopback-guarded (see <see cref="GuardOllamaEndpointIsLoopback" />).
    /// </summary>
    private static void AddOllamaRuntime(IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Capability gate: enabled unless explicitly disabled, so an un-flagged box keeps today's behavior exactly.
        if (!configuration.GetValue(OllamaRuntimeEnabledConfigurationKey, defaultValue: true))
        {
            return;
        }

        builder.Services.AddOllamaLocalModelProvider(sp =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(sp, configuration);
            GuardOllamaEndpointIsLoopback(chatConnectionSettings.Endpoint, configuration);
            return new OllamaLocalModelProviderRegistration(chatConnectionSettings.Endpoint, chatConnectionSettings.Model);
        });
    }

    /// <summary>
    ///     Rejects a non-loopback Ollama endpoint (SSRF). The local Ollama HTTP API is unauthenticated,
    ///     so a stray non-loopback endpoint value would route prompts to an arbitrary host. An operator can opt in to a
    ///     remote endpoint with <c>XE_OLLAMA_ALLOW_REMOTE_ENDPOINT=true</c>.
    /// </summary>
    private static void GuardOllamaEndpointIsLoopback(Uri endpoint, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(configuration);

        if (endpoint.IsLoopback || configuration.GetValue(OllamaAllowRemoteEndpointConfigurationKey, defaultValue: false))
        {
            return;
        }

        throw new InvalidOperationException($"The configured Ollama endpoint '{endpoint}' is not a loopback address. The local Ollama API is "
                                            + $"unauthenticated; refusing to route prompts to a remote host. Set {OllamaAllowRemoteEndpointConfigurationKey}=true to override.");
    }

    private static ModelRoutingLocalChatClient CreateLocalChatClient(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var chatConnectionSettings = ResolveChatConnectionSettings(serviceProvider, configuration);

        // The local branch is the ModelRoutingLocalChatClient. It routes per-send by
        // ChatOptions.ModelId through the provider resolver, so it supersedes BOTH the old fixed-model
        // ILocalModelProvider.CreateChatClient path and the raw-IOllamaApiClient-as-IChatClient fallback (the latter
        // could not route by ModelId for llama-server). XE_USE_LOCAL_MODEL_PROVIDER is still honored: when it is unset
        // the router's default provider (ollama) + the configured default model reproduce the previous single-daemon
        // behavior byte-for-byte for an un-mapped model; when set, the same router additionally honors any
        // llamacpp model_provider_map rows. The configured chat model is the fallback ModelId for requests that omit
        // ChatOptions.ModelId (mirrors the previous CreateLocalChatClient default).
        _ = UseLocalModelProvider(configuration);
        return new ModelRoutingLocalChatClient(serviceProvider.GetRequiredService<ILocalModelProviderResolver>(),
            chatConnectionSettings.Model);
    }

    private static void DispatchSafely(Task dispatchTask, ILogger logger, string operationName)
    {
        ArgumentNullException.ThrowIfNull(dispatchTask);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        _ = dispatchTask.ContinueWith(static (task, state) =>
            {
                var (continuationLogger, dispatchOperationName) = (DispatchContinuationState)(state ?? throw new ArgumentNullException(nameof(state)));

                if (task.IsFaulted)
                {
                    continuationLogger.LogError(task.Exception, "Unhandled worker hub event dispatch failure during {OperationName}.", dispatchOperationName);
                }
            },
            new DispatchContinuationState(logger, operationName),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private static ChatConnectionSettings ResolveChatConnectionSettings(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configuration);

        // The explicit "chat" connection string (the Aspire/dev orchestration override) still wins when present —
        // it is an out-of-band wiring channel, not a migrated user setting.
        var connectionString = configuration.GetConnectionString("chat");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var connectionStringBuilder = new DbConnectionStringBuilder
            {
                ConnectionString = connectionString
            };

            if (connectionStringBuilder.TryGetValue("Endpoint", out var endpointValue)
                && endpointValue is string endpoint
                && Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
                && connectionStringBuilder.TryGetValue("Model", out var modelValue)
                && modelValue is string model
                && !string.IsNullOrWhiteSpace(model))
            {
                return new ChatConnectionSettings(endpointUri, model);
            }
        }

        // Migrated knobs: the Ollama endpoint and the local-chat default model come from INodeRuntimeSettings
        // (stored > appsettings seed > hardcoded default). Read once at host build — an operator edit applies on the
        // next process restart. Ollama:ChatModel (an out-of-band runtime override, not a migrated setting)
        // still takes precedence over the migrated default model when configured.
        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
        var fallbackEndpoint = runtimeSettings.GetOllamaEndpoint();
        var fallbackModel = configuration.GetValue<string>("Ollama:ChatModel")
                            ?? runtimeSettings.GetDefaultModelName();

        return new ChatConnectionSettings(new Uri(fallbackEndpoint, UriKind.Absolute), fallbackModel);
    }

    /// <summary>
    ///     Builds the <see cref="HuggingFaceOptions" /> the HF GGUF store stack consumes, seeded from
    ///     <see cref="INodeRuntimeSettings" /> for the migrated knobs (<c>DefaultQuant</c>, <c>DiskMarginBytes</c>). The
    ///     config binding + <c>ModelsDirectory</c> defaulting mirror <c>AddHuggingFaceGgufStore</c> so the non-migrated
    ///     fields keep today's behavior; only the two migrated fields come from the accessor (stored &gt; seed &gt;
    ///     default). Resolved once at singleton construction — the blocking accessor read is not on any hot path.
    /// </summary>
    private static HuggingFaceOptions BuildSeededHuggingFaceOptions(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var options = new HuggingFaceOptions();
        configuration.GetSection(HuggingFaceOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            options.ModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        }

        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
        options.DefaultQuant = runtimeSettings.GetHuggingFaceDefaultQuant();
        options.DiskMarginBytes = runtimeSettings.GetHuggingFaceDiskMarginBytes();

        return options;
    }

    /// <summary>
    ///     Builds the <see cref="LlamaServerSupervisorOptions" /> the process supervisor + the provider resolver's
    ///     loaded-cap consume, with the migrated cap/TTL seeded from <see cref="INodeRuntimeSettings" /> (stored &gt;
    ///     seed &gt; default). The non-migrated port-range/restart fields keep their defaults. The supervisor reads these
    ///     as plain value-object fields on its hot reaper/spawn loop, so the one-time read here keeps that loop
    ///     allocation- and await-free; an operator cap/TTL edit applies on the next process restart.
    /// </summary>
    private static LlamaServerSupervisorOptions BuildSeededLlamaServerSupervisorOptions(IServiceProvider serviceProvider)
    {
        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
        return new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = runtimeSettings.GetLlamaMaxLoadedProcesses(),
            IdleTimeToLive = runtimeSettings.GetLlamaIdleTimeToLive(),

            // Chat-role launch flags: prompt-cache reuse + speculative decoding. Seeded here (like the cap/TTL) because
            // the provider option object cannot reach INodeRuntimeSettings (layer arrow Application → Providers). The
            // draft model is stored as a NAME and resolved to its GGUF path on the supervisor spawn path, the same way
            // the target model is — so the UI offers installed model names without knowing file paths. All of these are
            // captured at host build, so an operator edit applies on the next node restart.
            ChatCacheReuse = runtimeSettings.GetChatCacheReuse(),
            SpeculativeMode = runtimeSettings.GetSpeculativeMode(),
            SpeculativeDraftModelName = runtimeSettings.GetSpeculativeDraftModelName(),
            SpeculativeDraftMaxTokens = runtimeSettings.GetSpeculativeDraftMaxTokens(),
            SpeculativeDraftGpuLayers = runtimeSettings.GetSpeculativeDraftGpuLayers()
        };
    }

    /// <summary>
    ///     Seeds <see cref="LlamaServerLaunchPolicyOptions" /> from the node's KV-cache-type setting, registered before
    ///     <c>AddLlamaServerLocalModelProvider()</c> so the provider's own <c>TryAddSingleton</c> default becomes a
    ///     no-op. Every other member keeps its initializer default, so with the setting unset this object is equal to
    ///     <c>new LlamaServerLaunchPolicyOptions()</c> on every field its consumers read — the argv, the launch identity
    ///     and the inference-profile fingerprint are then byte-identical to a node that never had this knob.
    ///     <c>f16</c> collapses to <c>EnableGpuKvCacheQuantization = false</c>, which is exactly the
    ///     no-<c>-ctk</c>/<c>-ctv</c>/<c>-fa</c> vector a CPU spawn already emits.
    /// </summary>
    internal static LlamaServerLaunchPolicyOptions BuildSeededLlamaServerLaunchPolicyOptions(IServiceProvider serviceProvider)
    {
        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
        var kvCacheType = runtimeSettings.GetKvCacheType();
        return new LlamaServerLaunchPolicyOptions
        {
            KvCacheType = kvCacheType,
            EnableGpuKvCacheQuantization = !string.Equals(kvCacheType, LlamaServerKvCacheTypes.F16, StringComparison.Ordinal)
        };
    }

    private static bool UseLocalModelProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetValue<bool>(UseLocalModelProviderConfigurationKey);
    }

    private sealed record ChatConnectionSettings(Uri Endpoint, string Model);

    // The boxed state the fault continuation is handed. A named type instead of a cast to an anonymous tuple shape:
    // the continuation runs on a plain object?, and the cast has to match the boxed type exactly.
    private sealed record DispatchContinuationState(ILogger Logger, string OperationName);
}
