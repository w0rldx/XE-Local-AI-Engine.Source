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
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
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
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
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

        builder.Services.AddSingleton<NodeChatMigrationRecoveryService>();
        builder.Services.AddSingleton<INodeDbBackupService, NodeDbBackupService>();
        builder.Services.AddSingleton<IKnowledgeDowngradeSafetyService, KnowledgeDowngradeSafetyService>();

        // Node SQLite concurrency posture, resolved once: publish the pragma settings to the static raw-open helpers
        // (NodeSqlitePragmas.Configure — the raw-ADO OpenIfNeeded path takes no injected options) and register the interceptors applying them.
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
                   // EF's STATIC ServiceProviderCache keys on the options (incl. this connection string) and each entry roots the whole
                   // ServiceProvider: one host means one entry, while test hosts set this false to bypass the cache (docs/agent-knowledge.md §1).
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

        // Embeddings are provider-routed: EmbeddingPlaybookRetrievalRanker resolves the provider by PlaybookRetrievalOptions
        // .EmbeddingProviderName through ILocalModelProviderResolver and owns its generator per send. No IEmbeddingGenerator is DI-registered, by design.

        // Ollama is an OPTIONAL secondary local runtime: ALL Ollama-specific wiring lives in AddOllamaRuntime, so this module is the
        // single seam referencing Providers.Ollama — runtime selection keeps one capability gate, never a second provider-direct path.
        AddOllamaRuntime(builder, configuration);

        // The llama-server provider stack registers ALONGSIDE Ollama so the resolver can dispatch to either runtime: binary manager,
        // GPU variant probe, supervisor and the "llamacpp" provider. It takes two things from the host, an HttpClient and IGgufModelStore.
        builder.Services.AddHttpClient();
        // AddHuggingFaceGgufStore below provides the real IGgufModelStore (discovery, download, disk guard, registry); the optional
        // Hugging Face token rides the encrypted HfTokenStore, the third IDataProtector .enc store.
        builder.Services.AddSingleton<IHfTokenStore, HfTokenStore>();

        // Providers.* reference ONLY Providers.Abstractions, never Client.Application, so provider option objects are SEEDED from
        // INodeRuntimeSettings here at the composition root, each BEFORE its provider extension, whose TryAddSingleton default then no-ops.
        builder.Services.AddSingleton(sp => BuildSeededHuggingFaceOptions(sp, configuration));
        builder.Services.AddHuggingFaceGgufStore(configuration);

        builder.Services.AddSingleton(sp => BuildSeededLlamaServerSupervisorOptions(sp));
        builder.Services.AddSingleton(sp => BuildSeededLlamaServerLaunchPolicyOptions(sp));
        builder.Services.AddLlamaServerLocalModelProvider();
        builder.Services.AddSingleton<ILlamaCppRuntimeAdministrationService, LlamaCppRuntimeAdministrationService>();

        // The llama.cpp runtime / source-build / running-model endpoints' door onto the provider contracts, so no endpoint takes one
        // itself (the endpoint-dependency rule). Singleton, like all seven contracts it wraps, each a TryAddSingleton of the provider above.
        builder.Services.AddSingleton<LlamaCppRuntimeOrchestrationService>();

        // Opt-in local-model residency keeper: it polls live node settings and periodically touches the selected model, so the provider
        // reuses its resident process and refreshes idle age without blocking startup. Here, not in the host: it takes the supervisor contract.
        builder.Services.AddHostedService<KeepModelWarmBackgroundService>();

        // One-shot llama.cpp runtime update check: after a short non-blocking delay it resolves the recommended tag against the live
        // release catalog and records an "update available" snapshot for the runtime-status endpoint. Notify-only, offline-tolerant.
        builder.Services.AddHostedService<LlamaCppUpdateCheckService>();

        // The process-wide GPU-load admission gate: the REAL metric-emitting singleton shared by the llama-server and
        // stable-diffusion.cpp supervisors, so no two GPU loads race their --fit reads. A plain AddSingleton beats each provider's NoOpGpuModelLoadAdmission floor.
        builder.Services.AddSingleton(new GpuModelLoadAdmissionOptions());
        builder.Services.AddSingleton<IGpuModelLoadAdmission, GpuModelLoadAdmission>();

        // Inference Optimizer: profile-driven launch-arg replay, registered AFTER AddLlamaServerLocalModelProvider so the DB-backed
        // resolver OVERRIDES the provider's explore-only default (AddSingleton beats TryAddSingleton), keeping the arrow Application → Providers.
        builder.Services.AddSingleton<IMachineKeyProvider, MachineKeyProvider>();
        // A floor only: LlamaListDevicesProcessVramBudgetProbe from the LlamaServer provider overrides it over the same seam, so the
        // invalidation evaluator's live-VRAM check runs on supported backends and only this fallback skips it.
        builder.Services.TryAddSingleton<IProcessVramBudgetProbe, UnknownProcessVramBudgetProbe>();
        builder.Services.AddSingleton<IInferenceInvalidationEvaluator, InferenceInvalidationEvaluator>();
        // A singleton on the cold spawn path; it opens a fresh scope per resolve to reach the SCOPED IInferenceProfileStore.
        builder.Services.AddSingleton<IInferenceProfileResolver, InferenceProfileResolver>();

        // Per-model developer/advanced extra-launch-arg override, read on the cold spawn path. Registered last so it wins
        // over the provider's empty default; singleton that reads the scoped override store through a fresh scope per call.
        builder.Services.AddSingleton<ILlamaServerExtraLaunchArgumentsResolver, LlamaServerExtraLaunchArgumentsResolver>();

        // The provider resolver maps ModelName to ProviderName over the persisted model_provider_map, then ProviderName to an
        // ILocalModelProvider. Singleton, reading the scoped map store per lookup; unmapped names default to "llamacpp" (see docs/wiki/01-architecture-overview.md).
        builder.Services.AddSingleton<ILocalModelProviderResolver>(sp =>
        {
            var supervisorOptions = sp.GetRequiredService<LlamaServerSupervisorOptions>();
            return new LocalModelProviderResolver(sp.GetServices<ILocalModelProvider>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                LlamaServerProviderConstants.ProviderName,
                supervisorOptions.MaxLoadedProcesses,
                sp.GetRequiredService<TimeProvider>());
        });

        // The local-branch router is its own singleton so its (provider, model) chat-client cache can be invalidated out-of-band: the
        // runtime-update endpoint clears it after a variant switch, or a cached deferred client keeps dialling the now-gone endpoint.
        builder.Services.AddSingleton(sp => CreateLocalChatClient(sp, configuration));
        // The SAME instance backs the IChatClient local branch below, so invalidation and sends share one cache. Disposal is idempotent,
        // so the container disposing this singleton and RuntimeChatClient disposing its local branch is safe.
        builder.Services.AddSingleton<ILocalChatClientCacheInvalidator>(sp => sp.GetRequiredService<ModelRoutingLocalChatClient>());
        builder.Services.TryAddSingleton<ICloudEgressAuthorizer, DenyDevelopmentCloudEgressAuthorizer>();

        // A runtime-re-selecting IChatClient rather than one capturing the cloud-vs-local choice at startup: the wrapper re-evaluates the
        // active provider per send, so signing in or out needs no restart, and its local branch routes by ChatOptions.ModelId.
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

        // The node-configured, TIGHTEN-ONLY tool-approval policy. A plain AddSingleton, so it wins over the AI.Agent
        // PermissiveToolApprovalPolicy floor. Its JSON is read ONCE at construction, keeping the resolve path a lookup; edits need a restart.
#pragma warning disable MA0045 // DI factory delegate is synchronous by contract; INodeSettingsStore.Load is the documented sync twin of LoadAsync for the composition path.
        builder.Services.AddSingleton<IToolApprovalPolicy>(sp =>
            NodeToolApprovalPolicy.FromSettings(sp.GetRequiredService<INodeSettingsStore>().Load(CancellationToken.None)?.ToolApprovalPolicy));

        // The usage-summary cost resolver. Scoped, NOT singleton like the approval policy above, so each read reflects the CURRENT
        // operator rate override — the cached settings store makes Load() an in-memory hit, so rate edits apply without a restart.
        builder.Services.AddScoped<IUsageRateResolver>(sp =>
            UsageRateResolver.FromSettings(sp.GetRequiredService<INodeSettingsStore>().Load(CancellationToken.None)?.UsageRates));
#pragma warning restore MA0045

        // OrchestrationAgentOptions lives in AI.Agent, so its factory cannot inject INodeRuntimeSettings: the migrated IdleTimeoutSeconds
        // is seeded here through a DI-resolved Configure appended after the AI.Agent Bind, so a stored value wins over the appsettings seed.
        builder.Services.AddOptions<OrchestrationAgentOptions>()
#pragma warning disable MA0045 // Options Configure delegate is synchronous by contract; the INodeRuntimeSettings sync twin is the designated composition-path read.
               .Configure<INodeRuntimeSettings>((options, runtimeSettings) =>
                   options.IdleTimeoutSeconds = runtimeSettings.GetOrchestrationIdleTimeoutSeconds());
#pragma warning restore MA0045

        AddExternalOpenAiRuntime(builder);

        return builder;
    }

    /// <summary>
    ///     Registers the external OpenAI-compatible multiplexer provider, but ONLY when a real
    ///     <see cref="IExternalProviderRegistry" /> is already in the container.
    /// </summary>
    /// <remarks>
    ///     The guard exists because the provider has no behavior without the registry holding the operator's connections:
    ///     the resolver would route an <c>ext:</c> id to a provider reporting zero models, indistinguishable from "my
    ///     connections were silently dropped". Being a registration-TIME decision it reads the collection as built so
    ///     far and runs last in this module, so a composition root adding the external-provider store must do so before
    ///     <c>AddNodeModelRuntime</c> returns; the provider resolver enumerates providers at resolution time instead.
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
    ///     Registers the OPTIONAL Ollama local-model runtime as one cohesive, capability-gated block.
    /// </summary>
    /// <remarks>
    ///     This is the only place that references <c>Providers.Ollama</c>, so the resolver dispatches a model to this
    ///     provider or to llama.cpp through a single seam. The runtime is enabled unless <c>XE_OLLAMA_RUNTIME_ENABLED</c>
    ///     is false, and the resolved endpoint is loopback-guarded (<see cref="GuardOllamaEndpointIsLoopback" />). With
    ///     the gate OFF the provider stack is skipped, so this method supplies the two services whose Ollama-side
    ///     dependencies live inside it: <see cref="UnavailableModelCapabilityClient" /> and <see cref="UnavailableOllamaModelService" />.
    /// </remarks>
    private static void AddOllamaRuntime(IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Capability gate: enabled unless explicitly disabled, so an un-flagged box keeps today's behavior exactly.
        if (!configuration.GetValue(OllamaRuntimeEnabledConfigurationKey, defaultValue: true))
        {
            // Opting out of a SECONDARY runtime must not make the host unbuildable: ModelCapabilityProber and the model service are
            // mandatory singletons whose Ollama-side dependencies exist only in the skipped stack. Both no-ops report "nothing there".
            builder.Services.AddSingleton<IModelCapabilityClient, UnavailableModelCapabilityClient>();
            builder.Services.AddSingleton<IOllamaModelService, UnavailableOllamaModelService>();
            return;
        }

        builder.Services.AddOllamaLocalModelProvider(sp =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(sp, configuration);
            GuardOllamaEndpointIsLoopback(chatConnectionSettings.Endpoint, configuration);
            return new OllamaLocalModelProviderRegistration { Endpoint = chatConnectionSettings.Endpoint, Model = chatConnectionSettings.Model };
        });

        // IOllamaModelService's real registration rides inside AddOllamaLocalModelProvider, next to the IOllamaApiClient it wraps —
        // exactly once on this branch, mirroring the gate-off branch's UnavailableOllamaModelService above.
    }

    /// <summary>
    ///     Rejects a non-loopback Ollama endpoint (SSRF): the local Ollama HTTP API is unauthenticated, so a stray
    ///     non-loopback value would route prompts to an arbitrary host.
    /// </summary>
    /// <remarks>
    ///     An operator can opt in to a remote endpoint with <c>XE_OLLAMA_ALLOW_REMOTE_ENDPOINT</c> set to true.
    /// </remarks>
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

        // XE_USE_LOCAL_MODEL_PROVIDER is honored: unset, the router's default provider and the configured default model serve an
        // un-mapped model as a single daemon would; set, the same router additionally honors llamacpp model_provider_map rows.
        _ = UseLocalModelProvider(configuration);
        // The local branch routes per-send by ChatOptions.ModelId through the provider resolver, superseding the fixed-model
        // ILocalModelProvider.CreateChatClient path; the configured chat model is the fallback ModelId when a request omits one.
        return new ModelRoutingLocalChatClient(serviceProvider.GetRequiredService<ILocalModelProviderResolver>(),
            chatConnectionSettings.Model);
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
                return new ChatConnectionSettings { Endpoint = endpointUri, Model = model };
            }
        }

        // Migrated knobs: the Ollama endpoint and the local-chat default model come from INodeRuntimeSettings (stored > appsettings
        // seed > hardcoded default), read once at host build. Ollama:ChatModel, an out-of-band override, still wins over that default.
        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
#pragma warning disable MA0045 // The containing method is only ever reached from an AddSingleton(sp => …) DI factory delegate, which is synchronous by contract; the INodeRuntimeSettings sync twins are the designated composition-path reads.
        var fallbackEndpoint = runtimeSettings.GetOllamaEndpoint();
        var fallbackModel = configuration.GetValue<string>("Ollama:ChatModel")
                            ?? runtimeSettings.GetDefaultModelName();
#pragma warning restore MA0045

        return new ChatConnectionSettings { Endpoint = new Uri(fallbackEndpoint, UriKind.Absolute), Model = fallbackModel };
    }

    /// <summary>
    ///     Builds the <see cref="HuggingFaceOptions" /> the HF GGUF store stack consumes, seeded from
    ///     <see cref="INodeRuntimeSettings" /> for the migrated <c>DefaultQuant</c> and <c>DiskMarginBytes</c>.
    /// </summary>
    /// <remarks>
    ///     The config binding and <c>ModelsDirectory</c> defaulting mirror <c>AddHuggingFaceGgufStore</c>, so the
    ///     non-migrated fields keep today's behavior and only the two migrated ones come from the accessor
    ///     (stored &gt; seed &gt; default). Resolved once at singleton construction, off every hot path.
    /// </remarks>
    private static HuggingFaceOptions BuildSeededHuggingFaceOptions(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var options = new HuggingFaceOptions();
        configuration.GetSection(HuggingFaceOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.ModelsDirectory))
        {
            options.ModelsDirectory = Path.Combine(AppContext.BaseDirectory, "models");
        }

        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
#pragma warning disable MA0045 // The containing method is only ever reached from an AddSingleton(sp => …) DI factory delegate, which is synchronous by contract; the INodeRuntimeSettings sync twins are the designated composition-path reads.
        options.DefaultQuant = runtimeSettings.GetHuggingFaceDefaultQuant();
        options.DiskMarginBytes = runtimeSettings.GetHuggingFaceDiskMarginBytes();
#pragma warning restore MA0045

        return options;
    }

    /// <summary>
    ///     Builds the <see cref="LlamaServerSupervisorOptions" /> the process supervisor and the resolver's loaded-cap
    ///     consume, with the migrated cap/TTL seeded from <see cref="INodeRuntimeSettings" /> (stored &gt; seed &gt; default).
    /// </summary>
    /// <remarks>
    ///     The non-migrated port-range and restart fields keep their defaults. The supervisor reads these as plain
    ///     value-object fields on its hot reaper and spawn loop, so the one-time read here keeps that loop allocation-
    ///     and await-free; an operator cap or TTL edit applies on the next process restart.
    /// </remarks>
    private static LlamaServerSupervisorOptions BuildSeededLlamaServerSupervisorOptions(IServiceProvider serviceProvider)
    {
        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
#pragma warning disable MA0045 // The containing method is only ever reached from an AddSingleton(sp => …) DI factory delegate, which is synchronous by contract; the INodeRuntimeSettings sync twins are the designated composition-path reads.
        return new LlamaServerSupervisorOptions
        {
            MaxLoadedProcesses = runtimeSettings.GetLlamaMaxLoadedProcesses(),
            IdleTimeToLive = runtimeSettings.GetLlamaIdleTimeToLive(),

            // Chat-role launch flags: prompt-cache reuse and speculative decoding, seeded here (like the cap/TTL) because the provider
            // option object cannot reach INodeRuntimeSettings. The draft model is stored as a NAME, resolved to its GGUF path when spawning.
            ChatCacheReuse = runtimeSettings.GetChatCacheReuse(),
            SpeculativeMode = runtimeSettings.GetSpeculativeMode(),
            SpeculativeDraftModelName = runtimeSettings.GetSpeculativeDraftModelName(),
            SpeculativeDraftMaxTokens = runtimeSettings.GetSpeculativeDraftMaxTokens(),
            SpeculativeDraftGpuLayers = runtimeSettings.GetSpeculativeDraftGpuLayers()
        };
#pragma warning restore MA0045
    }

    /// <summary>
    ///     Seeds <see cref="LlamaServerLaunchPolicyOptions" /> from the node's KV-cache-type setting, registered before
    ///     <c>AddLlamaServerLocalModelProvider()</c> so the provider's own <c>TryAddSingleton</c> default becomes a no-op.
    /// </summary>
    /// <remarks>
    ///     Every other member keeps its initializer default, so with the setting unset this object equals
    ///     <c>new LlamaServerLaunchPolicyOptions()</c> on every field its consumers read: the argv, the launch identity
    ///     and the inference-profile fingerprint stay byte-identical to a node that never had this knob. <c>f16</c>
    ///     collapses to GPU KV-cache quantization disabled, exactly the no-<c>-ctk</c>/<c>-ctv</c>/<c>-fa</c> vector a
    ///     CPU spawn already emits.
    /// </remarks>
    internal static LlamaServerLaunchPolicyOptions BuildSeededLlamaServerLaunchPolicyOptions(IServiceProvider serviceProvider)
    {
        var runtimeSettings = serviceProvider.GetRequiredService<INodeRuntimeSettings>();
#pragma warning disable MA0045 // The containing method is only ever reached from an AddSingleton(sp => …) DI factory delegate, which is synchronous by contract; the INodeRuntimeSettings sync twins are the designated composition-path reads.
        var kvCacheType = runtimeSettings.GetKvCacheType();
#pragma warning restore MA0045
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

    private sealed record ChatConnectionSettings
    {
        public required Uri Endpoint { get; init; }

        public required string Model { get; init; }
    }

    // The boxed state the fault continuation is handed. A named type instead of a cast to an anonymous tuple shape:
    // the continuation runs on a plain object?, and the cast has to match the boxed type exactly.
}
