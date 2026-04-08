using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.BackgroundServices;
using XE_Local_AI_Engine.Components;
using XE_Local_AI_Engine.Configuration;
using XE_Local_AI_Engine.Configuration.Validation;
using XE_Local_AI_Engine.HealthChecks;
using XE_Local_AI_Engine.Services.Capabilities;
using XE_Local_AI_Engine.Services.Auth;
using XE_Local_AI_Engine.Services.Connection;
using XE_Local_AI_Engine.Services.Events;
using XE_Local_AI_Engine.Services.Invocation;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
       .AddInteractiveServerComponents();

builder.Services.AddOptions<CentralPlatformOptions>()
    .Bind(builder.Configuration.GetSection(CentralPlatformOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<WorkerNodeOptions>()
    .Bind(builder.Configuration.GetSection(WorkerNodeOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<CentralPlatformOptions>, CentralPlatformOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<WorkerNodeOptions>, WorkerNodeOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>();

var centralPlatformBaseUrl = builder.Configuration.GetValue<string>("CentralPlatform:BaseUrl")
    ?? throw new InvalidOperationException("CentralPlatform:BaseUrl is required.");

builder.Services.AddHttpClient("CentralPlatformApi", client =>
{
    client.BaseAddress = new Uri(centralPlatformBaseUrl, UriKind.Absolute);
});

builder.Services.AddSingleton<ITokenStore, TokenStore>();
builder.Services.AddSingleton<IPairingService, PairingService>();
builder.Services.AddSingleton<ConnectionState>();
builder.Services.AddSingleton(sp => new Lazy<IHubMessageSender>(() => sp.GetRequiredService<IHubMessageSender>()));
builder.Services.AddSingleton<IInvocationRunner, InvocationRunner>();
builder.Services.AddSingleton<IWorkerEventDispatcher, WorkerEventDispatcher>();
builder.Services.AddSingleton<ICapabilityReporter, CapabilityReporter>();
builder.Services.AddSingleton(sp => new Lazy<ICapabilityReporter>(() => sp.GetRequiredService<ICapabilityReporter>()));
builder.Services.AddSingleton<WorkerHubConnection>(sp =>
{
    var connection = ActivatorUtilities.CreateInstance<WorkerHubConnection>(sp);
    var dispatcher = sp.GetRequiredService<IWorkerEventDispatcher>();
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("WorkerHubConnectionEventBindings");

    connection.InvocationAssignedReceived += (_, args) =>
        DispatchSafely(dispatcher.DispatchInvocationAssignedAsync(args.RuntimePackage), logger, nameof(dispatcher.DispatchInvocationAssignedAsync));
    connection.ToolCallResultReceived += (_, args) =>
        DispatchSafely(dispatcher.DispatchToolCallResultAsync(args.ToolCallResult), logger, nameof(dispatcher.DispatchToolCallResultAsync));
    connection.DisconnectRequestedReceived += (_, args) =>
        DispatchSafely(dispatcher.DispatchDisconnectRequestedAsync(args.DisconnectRequest), logger, nameof(dispatcher.DispatchDisconnectRequestedAsync));
    connection.ApprovalResolvedReceived += (_, args) =>
        DispatchSafely(dispatcher.DispatchApprovalResolvedAsync(args.ApprovalResolution), logger, nameof(dispatcher.DispatchApprovalResolvedAsync));
    connection.InvocationCancelledReceived += (_, args) =>
        DispatchSafely(dispatcher.DispatchInvocationCancelledAsync(args.Cancellation), logger, nameof(dispatcher.DispatchInvocationCancelledAsync));

    return connection;
});
builder.Services.AddSingleton<IWorkerHubConnection>(sp => sp.GetRequiredService<WorkerHubConnection>());
builder.Services.AddSingleton<IHubMessageSender>(sp => sp.GetRequiredService<WorkerHubConnection>());
builder.Services.AddHostedService<HeartbeatBackgroundService>();
builder.Services.AddHostedService<AutoConnectBackgroundService>();
builder.Services.AddHostedService<ToolCallCleanupService>();
builder.Services.AddHealthChecks()
    .AddCheck<WorkerHealthCheck>("worker_health")
    .AddCheck<OllamaHealthCheck>("ollama_health", failureStatus: HealthStatus.Unhealthy);

// Load configuration
var ollamaEndpoint = builder.Configuration.GetValue<string>("Ollama:Endpoint") ?? "http://127.0.0.1:11434";
var chatModel = builder.Configuration.GetValue<string>("Ollama:ChatModel") ?? "qwen3.5:9b";
var ollamaUri = new Uri(ollamaEndpoint);

#pragma warning disable CA2000 // Dispose objects before losing scope - lifetime managed by DI container
IChatClient ollamaApiClient = new OllamaApiClient(ollamaUri, chatModel);
#pragma warning restore CA2000

builder.Services.AddSingleton<IChatClient>(_ => ollamaApiClient);

// Register AI Agent with dependency injection
builder.Services.AddSingleton<AIAgent>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Configuring AI Agent with Claude model '{Model}'", chatModel);

    var chatClient = sp.GetRequiredService<IChatClient>();
    return chatClient.CreateAIAgent(name: "ClaudeChat",
        instructions: "You are a helpful and friendly AI assistant.");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.UseStaticFiles();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

await app.RunAsync();

static void DispatchSafely(Task dispatchTask, ILogger logger, string operationName)
{
    ArgumentNullException.ThrowIfNull(dispatchTask);
    ArgumentNullException.ThrowIfNull(logger);
    ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

    _ = dispatchTask.ContinueWith(
        static (task, state) =>
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
