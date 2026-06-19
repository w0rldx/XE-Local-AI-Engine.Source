namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.HuggingFace;
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

        builder.Services.AddOllamaLocalModelProvider(_ =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(configuration);
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
        builder.Services.AddHuggingFaceGgufStore(configuration);
        builder.Services.AddLlamaServerLocalModelProvider();

        // The provider resolver maps ModelName→ProviderName (over the persisted model_provider_map,
        // unmapped → default) then ProviderName→ILocalModelProvider (over the registered set). Singleton; reads the
        // scoped map store through a fresh scope per lookup. DEFAULT for unmapped models = "ollama" — the
        // backfill keeps every existing model on its current runtime until it is explicitly re-pointed to llamacpp.
        // The supervisor's loaded-cap is surfaced for the preview reject-at-start check.
        builder.Services.AddSingleton<ILocalModelProviderResolver>(sp =>
        {
            var supervisorOptions = sp.GetRequiredService<LlamaServerSupervisorOptions>();
            return new LocalModelProviderResolver(sp.GetServices<ILocalModelProvider>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                OllamaLocalModelProvider.OllamaProviderName,
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

        return builder;
    }

    private static IChatClient CreateLocalChatClient(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var chatConnectionSettings = ResolveChatConnectionSettings(configuration);

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

    private static ChatConnectionSettings ResolveChatConnectionSettings(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

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

        var fallbackEndpoint = configuration.GetValue<string>("Ollama:Endpoint") ?? "http://127.0.0.1:11434";
        var fallbackModel = configuration.GetValue<string>("Ollama:ChatModel")
                            ?? configuration.GetValue<string>("Agent:LocalChat:DefaultModel")
                            ?? throw new InvalidOperationException("Agent:LocalChat:DefaultModel is required.");

        return new ChatConnectionSettings(new Uri(fallbackEndpoint, UriKind.Absolute), fallbackModel);
    }

    private static bool UseLocalModelProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration.GetValue<bool>(UseLocalModelProviderConfigurationKey);
    }

    private sealed record ChatConnectionSettings(Uri Endpoint, string Model);
}
