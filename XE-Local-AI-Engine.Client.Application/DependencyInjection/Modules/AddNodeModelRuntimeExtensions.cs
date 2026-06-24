namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.HuggingFace;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama;

internal static class AddNodeModelRuntimeExtensions
{
    private const string UseLocalModelProviderConfigurationKey = "XE_USE_LOCAL_MODEL_PROVIDER";

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

        builder.Services.AddDbContext<NodeChatDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString)
                   .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                   .AddInterceptors(serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
        });

        builder.Services.AddDbContext<NodeIdentityDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString,
                sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable));
        });

        // Embeddings are provider-routed: EmbeddingPlaybookRetrievalRanker resolves the embedding provider
        // by PlaybookRetrievalOptions.EmbeddingProviderName via ILocalModelProviderResolver and builds/owns its own
        // generator per send (node-local; ollama or llamacpp). There is intentionally no standalone DI-registered
        // IEmbeddingGenerator — the previous Ollama hardwire and its only consumer (the unused LocalEmbeddingService
        // adapter) were removed so nothing contradicts the multi-provider design.

        builder.Services.AddOllamaLocalModelProvider(sp =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(sp, configuration);
            return new OllamaLocalModelProviderRegistration(chatConnectionSettings.Endpoint, chatConnectionSettings.Model);
        });

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
        builder.Services.AddLlamaServerLocalModelProvider();

        // The provider resolver maps ModelName→ProviderName (over the persisted model_provider_map,
        // unmapped → default) then ProviderName→ILocalModelProvider (over the registered set). Singleton; reads the
        // scoped map store through a fresh scope per lookup. DEFAULT for unmapped models = "llamacpp" — post-epic Ollama
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

        // Register a runtime-re-selecting IChatClient rather than capturing the
        // cloud-vs-local choice once at startup. The wrapper re-evaluates the active provider per send via
        // IActiveCloudChatClientFactory, so signing in/out at runtime takes effect without a node restart.
        // The local branch is the ModelRoutingLocalChatClient — it routes per-send by
        // ChatOptions.ModelId across providers/processes rather than a single fixed-model client.
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var activeCloudFactory = sp.GetRequiredService<IActiveCloudChatClientFactory>();
            return new RuntimeChatClient(activeCloudFactory, () => CreateLocalChatClient(sp, configuration));
        });

        builder.Services.AddLocalAiAgentRuntime(builder.Configuration);

        // OrchestrationAgentOptions lives in AI.Agent (no reference to Client.Application), so OrchestrationAgentFactory
        // cannot inject INodeRuntimeSettings. Seed the migrated IdleTimeoutSeconds from the accessor here at the
        // composition root via a DI-resolved Configure action — it is appended after the AI.Agent Bind, so a stored
        // value overrides the appsettings seed. The factory caches options.Value at construction (operator edits apply
        // on the next process restart); the blocking accessor read runs once during options materialization, not on any hot path. The
        // accessor is resolved from the real container (no second ServiceProvider build).
        builder.Services.AddOptions<OrchestrationAgentOptions>()
               .Configure<INodeRuntimeSettings>((options, runtimeSettings) =>
                   options.IdleTimeoutSeconds = runtimeSettings.GetOrchestrationIdleTimeoutSeconds());

        return builder;
    }

    private static IChatClient CreateLocalChatClient(IServiceProvider serviceProvider, IConfiguration configuration)
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
                var (continuationLogger, dispatchOperationName) = ((ILogger Logger, string OperationName))state!;

                if (task.IsFaulted)
                {
                    continuationLogger.LogError(task.Exception, "Unhandled worker hub event dispatch failure during {OperationName}.", dispatchOperationName);
                }
            },
            (Logger: logger, OperationName: operationName),
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
            IdleTimeToLive = runtimeSettings.GetLlamaIdleTimeToLive()
        };
    }

    private static bool UseLocalModelProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetValue<bool>(UseLocalModelProviderConfigurationKey);
    }

    private sealed record ChatConnectionSettings(Uri Endpoint, string Model);
}
