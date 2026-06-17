namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Analysis;
using XE_Local_AI_Engine.Client.Services.Analysis.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;
using XE_Local_AI_Engine.Client.Services.Insights;
using XE_Local_AI_Engine.Client.Services.Insights.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.Client.Services.Manager.Implementation;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Mcp.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Monitoring;
using XE_Local_AI_Engine.Client.Services.Monitoring.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama;
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

internal static class AddNodeModelRuntimeExtensions
{
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

        // Embeddings are provider-routed (Lane A §7.7): EmbeddingPlaybookRetrievalRanker resolves the embedding provider
        // by PlaybookRetrievalOptions.EmbeddingProviderName via ILocalModelProviderResolver and builds/owns its own
        // generator per send (node-local; ollama or llamacpp). There is intentionally no standalone DI-registered
        // IEmbeddingGenerator — the previous Ollama hardwire and its only consumer (the unused LocalEmbeddingService
        // adapter) were removed so nothing contradicts the multi-provider design.

        builder.Services.AddOllamaLocalModelProvider(_ =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(configuration);
            return new OllamaLocalModelProviderRegistration(chatConnectionSettings.Endpoint, chatConnectionSettings.Model);
        });

        // Lane A (decision #14): register the llama-server provider stack ALONGSIDE Ollama so the resolver can
        // dispatch a model to either runtime. AddLlamaServerLocalModelProvider adds the binary manager, the GPU
        // variant probe, the process supervisor, and the "llamacpp" ILocalModelProvider into the provider set.
        // Caller-contract dependencies (the provider project intentionally takes them from the host):
        //   • an HttpClient for binary downloads + health probes (AddHttpClient),
        //   • an IGgufModelStore — Lane B's real HF GGUF store later; until then FixedPathGgufModelStore is the
        //     LANE B SWAP POINT (replace this single registration when Lane B lands).
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IGgufModelStore>(_ =>
            new FixedPathGgufModelStore(ResolveGgufModelStorePlaceholderPath(configuration)));
        builder.Services.AddLlamaServerLocalModelProvider();

        // Lane A (§7.5): the provider resolver maps ModelName→ProviderName (over the persisted model_provider_map,
        // unmapped → default) then ProviderName→ILocalModelProvider (over the registered set). Singleton; reads the
        // scoped map store through a fresh scope per lookup. DEFAULT for unmapped models = "ollama" — the §6.1
        // backfill keeps every existing model on its current runtime until it is explicitly re-pointed to llamacpp
        // (no GGUF binary path exists yet; Lane B/C land that). The supervisor's loaded-cap is surfaced for the
        // preview reject-at-start check (T5/§7.6).
        builder.Services.AddSingleton<ILocalModelProviderResolver>(sp =>
        {
            var supervisorOptions = sp.GetRequiredService<LlamaServerSupervisorOptions>();
            return new LocalModelProviderResolver(
                sp.GetServices<ILocalModelProvider>(),
                sp.GetRequiredService<IServiceScopeFactory>(),
                defaultProviderName: OllamaLocalModelProvider.OllamaProviderName,
                maxLoadedProcesses: supervisorOptions.MaxLoadedProcesses);
        });

        // C2 fix (plan §0/§7.2): register a runtime-re-selecting IChatClient rather than capturing the
        // cloud-vs-local choice once at startup. The wrapper re-evaluates the active provider per send via
        // IActiveCloudChatClientFactory, so signing in/out at runtime takes effect without a node restart.
        // Lane A (§7.4): the local branch is now the ModelRoutingLocalChatClient — it routes per-send by
        // ChatOptions.ModelId across providers/processes rather than a single fixed-model client.
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var activeCloudFactory = sp.GetRequiredService<IActiveCloudChatClientFactory>();
            return new RuntimeChatClient(activeCloudFactory, () => CreateLocalChatClient(sp, configuration));
        });

        builder.Services.AddLocalAiAgentRuntime(builder.Configuration);

        return builder;
    }

    private const string UseLocalModelProviderConfigurationKey = "XE_USE_LOCAL_MODEL_PROVIDER";

    private static IChatClient CreateLocalChatClient(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var chatConnectionSettings = ResolveChatConnectionSettings(configuration);

        // Lane A (§7.4/§7.5): the local branch is the ModelRoutingLocalChatClient. It routes per-send by
        // ChatOptions.ModelId through the provider resolver, so it supersedes BOTH the old fixed-model
        // ILocalModelProvider.CreateChatClient path and the raw-IOllamaApiClient-as-IChatClient fallback (the latter
        // could not route by ModelId for llama-server). XE_USE_LOCAL_MODEL_PROVIDER is still honored: when it is unset
        // the router's default provider (ollama) + the configured default model reproduce the previous single-daemon
        // behavior byte-for-byte for an un-mapped model; when set, the same router additionally honors any
        // llamacpp model_provider_map rows. The configured chat model is the fallback ModelId for requests that omit
        // ChatOptions.ModelId (mirrors the previous CreateLocalChatClient default).
        _ = UseLocalModelProvider(configuration);
        return new ModelRoutingLocalChatClient(
            serviceProvider.GetRequiredService<ILocalModelProviderResolver>(),
            chatConnectionSettings.Model);
    }

    /// <summary>
    ///     Resolves the placeholder GGUF path handed to the <see cref="FixedPathGgufModelStore" /> stub. This is the
    ///     LANE B SWAP POINT: the real HF GGUF store replaces the store registration entirely, so this path is only
    ///     consulted if a model is explicitly mapped to <c>llamacpp</c> before Lane B lands (no model is by default).
    /// </summary>
    private static string ResolveGgufModelStorePlaceholderPath(IConfiguration configuration)
    {
        return configuration.GetValue<string>("LlamaServer:GgufModelPath")
               ?? Path.Combine(AppContext.BaseDirectory, "models", "placeholder.gguf");
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
