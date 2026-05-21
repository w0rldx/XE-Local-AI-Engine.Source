namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using OllamaSharp;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Embeddings;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Manager;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Shutdown;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Ollama;
using ILogger = ILogger;

public static class ConfigureServices
{
    private const string ConsoleOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";
    private const string UseLocalModelProviderConfigurationKey = "XE_USE_LOCAL_MODEL_PROVIDER";

    public static void AddServices(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        // Serilog
        _ = builder.Services.AddSerilog((serviceCollection, lc) => lc
                                                                   .ReadFrom.Configuration(configuration)
                                                                   .ReadFrom.Services(serviceCollection)
                                                                   .Enrich.FromLogContext()
                                                                   .WriteTo.Console(theme: ConsoleTheme.None, outputTemplate: ConsoleOutputTemplate));

        builder.Services.AddRazorComponents()
               .AddInteractiveServerComponents();
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddMudServices();

        builder.Services.AddOptions<CentralPlatformOptions>()
               .Bind(configuration.GetSection(CentralPlatformOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerNodeOptions>()
               .Bind(configuration.GetSection(WorkerNodeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<SecurityOptions>()
               .Bind(configuration.GetSection(SecurityOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<CloudProviderOptions>()
               .Bind(configuration.GetSection(CloudProviderOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddOptions<WorkerShutdownDrainOptions>();

        builder.Services.AddSingleton<IValidateOptions<CentralPlatformOptions>, CentralPlatformOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<WorkerNodeOptions>, WorkerNodeOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<CloudProviderOptions>, CloudProviderOptionsValidator>();

        var centralPlatformBaseUrl = configuration.GetValue<string>("CentralPlatform:BaseUrl")
                                     ?? throw new InvalidOperationException("CentralPlatform:BaseUrl is required.");

        if (!centralPlatformBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (builder.Environment.IsDevelopment())
            {
                Console.WriteLine("WARNING: CentralPlatform:BaseUrl is not HTTPS. Tokens may be transmitted in plaintext.");
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
        builder.Services.AddScoped<AuthenticationStateProvider, LocalOperatorAuthenticationStateProvider>();
        builder.Services.AddSingleton<ICloudCredentialStore, CloudCredentialStore>();
        builder.Services.AddSingleton<INodeSettingsStore, NodeSettingsStore>();
        builder.Services.AddSingleton<IAzureFoundryChatClientFactory, AzureFoundryChatClientFactory>();
        builder.Services.AddSingleton<INodeKeyRegistry, NodeKeyRegistry>();
        builder.Services.AddSingleton<IPairingService, PairingService>();
        builder.Services.AddSingleton<IWorkerTokenRefreshService, WorkerTokenRefreshService>();
        builder.Services.AddScoped<INodeBindingService, NodeBindingService>();
        builder.Services.AddSingleton<ConnectionState>();
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
        builder.Services.AddScoped<NodeEncryptionSaveChangesInterceptor>();
        builder.Services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        builder.Services.AddScoped<INodeRetentionStore, NodeRetentionStore>();
        builder.Services.AddSingleton<DeadLetterFlushService>();
        builder.Services.AddSingleton<IWorkerShutdownDrainService, WorkerShutdownDrainService>();
        builder.Services.AddSingleton<IOllamaModelService, OllamaModelService>();
        builder.Services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        builder.Services.AddScoped<ILocalChatInvocationService, LocalChatInvocationService>();
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
        builder.Services.AddHostedService<HeartbeatBackgroundService>();
        builder.Services.AddHostedService<AutoConnectBackgroundService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<RetentionSweeperService>();
        builder.Services.AddHostedService<ToolCallCleanupService>();
        builder.Services.AddHealthChecks()
               .AddCheck<WorkerHealthCheck>("worker_health", tags: ["ready"])
               .AddCheck<OllamaHealthCheck>("ollama_health", HealthStatus.Unhealthy, ["ready"]);

        builder.Services.AddDbContext<NodeChatDbContext>((serviceProvider, options) =>
        {
            var connectionString = configuration.GetConnectionString("node-sqlite")
                                   ?? throw new InvalidOperationException("Connection string 'node-sqlite' is required.");

            options.UseSqlite(connectionString)
                   .AddInterceptors(serviceProvider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       serviceProvider.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
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
    }

    private static void DispatchSafely(Task dispatchTask, ILogger logger, string operationName)
    {
        ArgumentNullException.ThrowIfNull(dispatchTask);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        _ = dispatchTask.ContinueWith(static (task, state) =>
            {
                var (continuationLogger, dispatchOperationName) = ((Serilog.ILogger Logger, string OperationName))state!;

                if (task.IsFaulted)
                {
                    continuationLogger.Error(task.Exception, "Unhandled worker hub event dispatch failure during {OperationName}.", dispatchOperationName);
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
