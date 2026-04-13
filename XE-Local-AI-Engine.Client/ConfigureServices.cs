namespace XE_Local_AI_Engine.Client;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MudBlazor.Services;
using OllamaSharp;
using Serilog;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.Embeddings;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Validation;

public static class ConfigureServices
{
    public static void AddServices(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        // Serilog
        _ = builder.Services.AddSerilog((serviceCollection, lc) => lc
                                                                   .ReadFrom.Configuration(configuration)
                                                                   .ReadFrom.Services(serviceCollection)
                                                                   .Enrich.FromLogContext());

        builder.AddServiceDefaults();
        builder.Services.AddRazorComponents()
               .AddInteractiveServerComponents();
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

        builder.Services.AddSingleton<IValidateOptions<CentralPlatformOptions>, CentralPlatformOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<WorkerNodeOptions>, WorkerNodeOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();

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
        builder.Services.AddSingleton<IPairingService, PairingService>();
        builder.Services.AddSingleton<ConnectionState>();
        builder.Services.AddSingleton(sp => new Lazy<IHubMessageSender>(() => sp.GetRequiredService<IHubMessageSender>()));
        builder.Services.AddSingleton<ModelNameValidator>();
        builder.Services.AddSingleton<IRuntimePackageValidator, RuntimePackageValidator>();
        builder.Services.AddSingleton<IInvocationRunner, InvocationRunner>();
        builder.Services.AddSingleton<IWorkerEventDispatcher, WorkerEventDispatcher>();
        builder.Services.AddSingleton<ICapabilityReporter, CapabilityReporter>();
        builder.Services.AddSingleton(sp => new Lazy<ICapabilityReporter>(() => sp.GetRequiredService<ICapabilityReporter>()));
        builder.Services.AddSingleton<IDeadLetterStore, FileDeadLetterStore>();
        builder.Services.AddSingleton<DeadLetterFlushService>();
        builder.Services.AddSingleton<ILocalToolExecutor, LocalToolExecutor>();
        builder.Services.AddSingleton<IOllamaModelService, OllamaModelService>();
        builder.Services.AddSingleton<ILocalEmbeddingService, LocalEmbeddingService>();
        builder.Services.AddScoped<LocalChatService>();
        builder.Services.AddSingleton<WorkerHubConnection>(sp =>
        {
            var connection = ActivatorUtilities.CreateInstance<WorkerHubConnection>(sp);
            var dispatcher = new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>());
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("WorkerHubConnectionEventBindings");

            connection.InvocationAssignedReceived += (_, args) =>
                DispatchSafely(dispatcher.Value.DispatchInvocationAssignedAsync(args.RuntimePackage), logger, nameof(IWorkerEventDispatcher.DispatchInvocationAssignedAsync));
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
        builder.Services.AddHostedService<HeartbeatBackgroundService>();
        builder.Services.AddHostedService<AutoConnectBackgroundService>();
        builder.Services.AddHostedService<ToolCallCleanupService>();
        builder.Services.AddHealthChecks()
               .AddCheck<WorkerHealthCheck>("worker_health", tags: ["ready"])
               .AddCheck<OllamaHealthCheck>("ollama_health", HealthStatus.Unhealthy, ["ready"]);

        builder.AddOllamaApiClient("chat")
               .AddChatClient();
        builder.AddOllamaApiClient("embeddings")
               .AddEmbeddingGenerator();

        // Register AI Agent with dependency injection
        builder.Services.AddSingleton<AIAgent>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Program>>();
            logger.LogInformation("Configuring AI Agent with Ollama chat client.");

            var chatClient = sp.GetRequiredService<IChatClient>();
            return chatClient.CreateAIAgent(name: "ClaudeChat",
                instructions: "You are a helpful and friendly AI assistant.");
        });
    }

    private static void DispatchSafely(Task dispatchTask, Microsoft.Extensions.Logging.ILogger logger, string operationName)
    {
        ArgumentNullException.ThrowIfNull(dispatchTask);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        _ = dispatchTask.ContinueWith(static (task, state) =>
            {
                var (continuationLogger, dispatchOperationName) = ((ILogger Logger, string OperationName))state!;

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
}
