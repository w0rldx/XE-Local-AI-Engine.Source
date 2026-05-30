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
using XE_Local_AI_Engine.Client.Services.Embeddings;
using XE_Local_AI_Engine.Client.Services.Embeddings.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Client.Services.HostAgent.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.Client.Services.Manager.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Shutdown.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama;
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

public static class NodeApplicationServiceCollectionExtensions
{
    private const string UseLocalModelProviderConfigurationKey = "XE_USE_LOCAL_MODEL_PROVIDER";

    public static IHostApplicationBuilder AddNodeApplication(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<CentralPlatformOptions>()
               .Bind(configuration.GetSection(CentralPlatformOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerNodeOptions>()
               .Bind(configuration.GetSection(WorkerNodeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<ClientSecurityOptions>()
               .Bind(configuration.GetSection(ClientSecurityOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<NodeAuthOptions>()
               .Bind(configuration.GetSection(NodeAuthOptions.SectionName))
               .ValidateDataAnnotations()
               .Validate(static options => !string.IsNullOrWhiteSpace(options.Jwt.Issuer), "NodeAuth:Jwt:Issuer is required.")
               .Validate(static options => !string.IsNullOrWhiteSpace(options.Jwt.Audience), "NodeAuth:Jwt:Audience is required.")
               .Validate(static options => options.Jwt.AccessTokenMinutes is >= 1 and <= 1440, "NodeAuth:Jwt:AccessTokenMinutes must be between 1 and 1440.")
               .Validate(static options => options.RefreshTokenDays is >= 1 and <= 365, "NodeAuth:RefreshTokenDays must be between 1 and 365.")
               .ValidateOnStart();
        builder.Services.AddOptions<CloudProviderOptions>()
               .Bind(configuration.GetSection(CloudProviderOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<NodeChatMigrationRecoveryOptions>()
               .Bind(configuration.GetSection(NodeChatMigrationRecoveryOptions.SectionName))
               .Validate(static options => options.MigrationAttemptTimeout > TimeSpan.Zero, "Migration attempt timeout must be greater than zero.")
               .Validate(static options => options.StartupLockTimeout > TimeSpan.Zero, "Startup lock timeout must be greater than zero.")
               .Validate(static options => options.StartupLockPollInterval > TimeSpan.Zero, "Startup lock poll interval must be greater than zero.")
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerShutdownDrainOptions>();

        builder.Services.AddSingleton<IValidateOptions<CentralPlatformOptions>, CentralPlatformOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<WorkerNodeOptions>, WorkerNodeOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<ClientSecurityOptions>, SecurityOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<CloudProviderOptions>, CloudProviderOptionsValidator>();

        var centralPlatformBaseUrl = configuration.GetValue<string>("CentralPlatform:BaseUrl")
                                     ?? throw new InvalidOperationException("CentralPlatform:BaseUrl is required.");

        if (!centralPlatformBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (builder.Environment.IsDevelopment())
            {
                Console.Error.WriteLine("WARNING: CentralPlatform:BaseUrl is not HTTPS. Tokens may be transmitted in plaintext.");
            }
            else
            {
                throw new InvalidOperationException("CentralPlatform:BaseUrl must use HTTPS in non-development environments.");
            }
        }

        builder.Services.AddHttpClient("CentralPlatformApi", client =>
        {
            client.BaseAddress = new Uri(centralPlatformBaseUrl, UriKind.Absolute);
        }).AddStandardResilienceHandler();

        builder.Services.AddSingleton<ITokenStore, TokenStore>();
        builder.Services.AddSingleton<INodeOperatorSecretProvider, NodeOperatorSecretProvider>();
        builder.Services.AddSingleton<INodeJwtKeyProvider, NodeJwtKeyProvider>();
        builder.Services.AddSingleton<INodeTokenService, NodeTokenService>();
        builder.Services.AddScoped<INodeAuthService, NodeAuthService>();
        builder.Services.AddSingleton<NodeIdentityInitializationService>();
        builder.Services.AddSingleton<ICloudCredentialStore, CloudCredentialStore>();
        builder.Services.AddSingleton<INodeSettingsStore, NodeSettingsStore>();
        builder.Services.AddSingleton<IAzureFoundryChatClientFactory, AzureFoundryChatClientFactory>();
        builder.Services.AddSingleton<INodeKeyRegistry, NodeKeyRegistry>();
        builder.Services.AddSingleton<IPairingService, PairingService>();
        builder.Services.AddSingleton<IWorkerTokenRefreshService, WorkerTokenRefreshService>();
        builder.Services.AddSingleton<INodeBindingService, NodeBindingService>();
        builder.Services.AddSingleton<ConnectionState>();
        builder.Services.AddSingleton<IConnectionControlService, ConnectionControlService>();
        builder.Services.AddSingleton(HostAgentClientOptions.FromConfiguration(configuration));
        builder.Services.AddSingleton<IHostAgentClient, GrpcHostAgentClient>();
        builder.Services.AddSingleton(HostAgentStartupGateOptions.FromConfiguration(configuration));
        builder.Services.AddSingleton<IHostAgentReadinessClient>(sp =>
        {
            var options = sp.GetRequiredService<HostAgentStartupGateOptions>();
            return options.Enabled
                ? ActivatorUtilities.CreateInstance<GrpcHostAgentReadinessClient>(sp)
                : new DisabledHostAgentReadinessClient();
        });
        builder.Services.AddSingleton(sp => new Lazy<IHubMessageSender>(() => sp.GetRequiredService<IHubMessageSender>()));
        builder.Services.AddSingleton(sp => new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>()));
        builder.Services.AddSingleton<ModelNameValidator>();
        builder.Services.AddSingleton<IRuntimePackageValidator, RuntimePackageValidator>();
        builder.Services.AddSingleton<IHostAgentManagerService, HostAgentManagerService>();
        builder.Services.AddSingleton<IEnvelopeCryptoService, EnvelopeCryptoService>();
        builder.Services.AddSingleton<IRuntimePackageEnvelopeAssembler, RuntimePackageEnvelopeAssembler>();
        builder.Services.AddSingleton<IInvocationRunner, InvocationRunner>();
        builder.Services.AddSingleton<IInvocationHistory, InvocationHistory>();
        builder.Services.AddSingleton<IWorkerEventDispatcher, WorkerEventDispatcher>();
        builder.Services.AddSingleton<ICapabilityReporter, CapabilityReporter>();
        builder.Services.AddSingleton(sp => new Lazy<ICapabilityReporter>(() => sp.GetRequiredService<ICapabilityReporter>()));
        builder.Services.AddSingleton<IDeadLetterStore, FileDeadLetterStore>();
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NodeSqliteKeyHolder>();
        builder.Services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        builder.Services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        builder.Services.AddScoped<INodeRetentionStore, NodeRetentionStore>();
        // Marker E selected-folder store plus the safe resolver. The store persists folders in node SQLite with the
        // host path encrypted at rest by the node encryption interceptors. The resolver owns alias normalization, host
        // path validation, and exposes only the opaque id and alias to the model. Registration stays reachable even
        // when AgentHome is disabled; workspace copy (Marker F) and the tool (Marker I) consume it.
        builder.Services.AddScoped<INodeSelectedFolderStore, NodeSelectedFolderStore>();
        builder.Services.AddScoped<ISelectedFolderResolver, SelectedFolderResolver>();
        // Marker F sensitive-file exclusion policy for the workspace copy (stateless, name-based).
        builder.Services.AddSingleton<ISensitiveFileExclusionService, SensitiveFileExclusionService>();
        builder.Services.AddSingleton<DeadLetterFlushService>();
        builder.Services.AddSingleton<IWorkerShutdownDrainService, WorkerShutdownDrainService>();
        builder.Services.AddSingleton<IOllamaModelService, OllamaModelService>();
        builder.Services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        builder.Services.AddSingleton<ILocalToolOfferProvider, LocalToolOfferProvider>();
        // Option B ClientLocal tool run_in_agent_home. Marker I-pre replaces the pending placeholder with the real
        // fake-backed gateway: the handler still flag-gates and §7-validates, then delegates through the gateway to
        // IAgentHomeService, which drives the manifest initializer, the sandbox provider (the fake by default), and
        // the selected-folder resolver. The tool stays off the distributed wire (server seed inactive) until Marker I.
        builder.Services.AddSingleton<IAgentHomeIdentityProvider, AgentHomeIdentityProvider>();
        // Marker F workspace copy: PrepareAsync delegates the selected-folder copy (exclusions, symlink-escape guard,
        // byte budget, git baseline) to this stateless service.
        builder.Services.AddSingleton<IAgentHomeWorkspaceService, AgentHomeWorkspaceService>();
        // Marker G patch export: RunAsync delegates the post-run diff of the Marker F baseline (changes.patch +
        // changed-files.json, MaxPatchBytes budget) to this stateless service.
        builder.Services.AddSingleton<IAgentHomePatchService, AgentHomePatchService>();
        // Marker H memory proposal export: RunAsync delegates the gated collect of the agent-written JSONL proposals
        // (schema validation + secret scan) to this stateless service.
        builder.Services.AddSingleton<IAgentHomeMemoryProposalService, AgentHomeMemoryProposalService>();
        // Marker K run-scoped JSONL logger. Lane 4 (Marker I) constructs one per run via the factory; base
        // JSONL logging + redaction contract is the MVP scope. OTel meters and list-runs endpoint are deferred.
        builder.Services.AddTransient<IAgentHomeRunLogger, AgentHomeRunLogger>();
        builder.Services.AddSingleton<IAgentHomeService, AgentHomeService>();
        builder.Services.AddSingleton<IAgentHomeToolGateway, AgentHomeToolGateway>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RunInAgentHomeToolHandler>();
        // Marker C sandbox provider abstraction. Selection is configuration-bound and restart-required (resolved once
        // as a singleton). The MVP default is the deterministic fake; Marker J-local adds "local-container".
        builder.Services.AddOptions<SandboxOptions>()
               .Bind(configuration.GetSection(SandboxOptions.SectionName));
        builder.Services.AddSingleton<ISandboxRuntimeProvider>(SandboxProviderSelector.Resolve);
        // Marker D AgentHome layout initializer. Materializes the worker-local /agent-home tree (idempotent,
        // self-healing, owner-mismatch-recovering). Wired but unreached in production until Marker I-pre swaps the
        // pending gateway for a fake-backed one; the layout itself can initialize while AgentHome:Enabled=false.
        builder.Services.AddOptions<AgentHomeOptions>()
               .Bind(configuration.GetSection(AgentHomeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<AgentHomeOptions>, AgentHomeOptionsValidator>();
        builder.Services.AddSingleton<IAgentHomeManifestService, AgentHomeManifestService>();
        builder.Services.AddSingleton<NodeChatPersistenceWriter>();
        builder.Services.AddSingleton<INodeChatPersistenceService, NodeChatPersistenceService>();
        builder.Services.AddSingleton<INodeChatInvocationPump, NodeChatInvocationPump>();
        builder.Services.AddSingleton<INodeChatRemotePersistenceCoordinator, NodeChatRemotePersistenceCoordinator>();
        builder.Services.AddSingleton<INodeChatMutationGuard, NodeChatMutationGuard>();
        builder.Services.AddSingleton<INodeChatStreamCancellationRegistry, NodeChatStreamCancellationRegistry>();
        builder.Services.AddSingleton<IInvocationResumeRegistry, InvocationResumeRegistry>();
        builder.Services.AddScoped<INodeChatStreamService, NodeChatStreamService>();
        builder.Services.AddScoped<INodeChatRegenerationService, NodeChatRegenerationService>();
        builder.Services.AddSingleton<NodeChatRestartRecoveryService>();
        builder.Services.AddSingleton<ILocalEmbeddingService, LocalEmbeddingService>();
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

        builder.AddOllamaApiClient("embeddings")
               .AddEmbeddingGenerator();

        builder.Services.AddOllamaLocalModelProvider(_ =>
        {
            var chatConnectionSettings = ResolveChatConnectionSettings(configuration);
            return new OllamaLocalModelProviderRegistration(chatConnectionSettings.Endpoint, chatConnectionSettings.Model);
        });
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var credentialStore = sp.GetRequiredService<ICloudCredentialStore>();
            var credentials = credentialStore.LoadAsync().GetAwaiter().GetResult();

            if (credentials is not null
                && string.Equals(credentials.ProviderName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
            {
                return sp.GetRequiredService<IAzureFoundryChatClientFactory>().Create(credentials);
            }

            var chatConnectionSettings = ResolveChatConnectionSettings(configuration);
            if (UseLocalModelProvider(configuration))
            {
                return sp.GetRequiredService<ILocalModelProvider>().CreateChatClient(new LocalModelSelection
                {
                    ModelName = chatConnectionSettings.Model,
                    ProviderName = OllamaLocalModelProvider.OllamaProviderName
                });
            }

            return sp.GetRequiredService<IOllamaApiClient>() as IChatClient
                   ?? throw new InvalidOperationException("The configured local Ollama client must implement IChatClient.");
        });

        builder.Services.AddLocalAiAgentRuntime(builder.Configuration);

        return builder;
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
