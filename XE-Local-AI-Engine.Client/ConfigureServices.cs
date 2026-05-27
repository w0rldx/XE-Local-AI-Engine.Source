namespace XE_Local_AI_Engine.Client;

using System.Data.Common;
using System.Threading.RateLimiting;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSwag;
using OllamaSharp;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
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
using ClientSecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;
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

        builder.Services.ConfigureHttpJsonOptions(options => ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddFastEndpoints(options =>
        {
            options.DisableAutoDiscovery = true;
            options.Assemblies = [typeof(ConfigureServices).Assembly];
        });
        builder.Services.AddSignalR(options =>
        {
            options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            options.MaximumReceiveMessageSize = 64 * 1024;
            options.StreamBufferCapacity = 1;
        });
        builder.Services.SwaggerDocument(options =>
        {
            options.DocumentSettings = settings =>
            {
                settings.DocumentName = "v1";
                settings.Title = "XE Local AI Engine";
                settings.Version = "v1";
                settings.AddAuth("Bearer", new OpenApiSecurityScheme
                {
                    Type = OpenApiSecuritySchemeType.Http,
                    Scheme = JwtBearerDefaults.AuthenticationScheme,
                    BearerFormat = "JWT"
                });
            };

            options.ExcludeNonFastEndpoints = true;
        });
        builder.Services.AddIdentityCore<NodeUser>(options =>
               {
                   options.Password.RequiredLength = 12;
                   options.User.RequireUniqueEmail = true;
                   options.SignIn.RequireConfirmedAccount = false;
                   options.Lockout.AllowedForNewUsers = true;
                   options.Lockout.MaxFailedAccessAttempts = 5;
                   options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                   options.ClaimsIdentity.UserIdClaimType = JwtRegisteredClaimNames.Sub;
                   options.ClaimsIdentity.UserNameClaimType = JwtRegisteredClaimNames.Name;
                   options.ClaimsIdentity.RoleClaimType = NodeAuthorizationPolicies.RoleClaimType;
                })
               .AddRoles<IdentityRole>()
               .AddEntityFrameworkStores<NodeIdentityDbContext>()
               .AddSignInManager();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
               .AddJwtBearer();
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
               .Configure<IOptions<NodeAuthOptions>, INodeJwtKeyProvider>((options, nodeAuthOptions, jwtKeyProvider) =>
               {
                   var jwtOptions = nodeAuthOptions.Value.Jwt;
                   options.MapInboundClaims = false;
                   options.TokenValidationParameters = new TokenValidationParameters
                   {
                       ValidateIssuer = true,
                       ValidIssuer = jwtOptions.Issuer,
                       ValidateAudience = true,
                       ValidAudience = jwtOptions.Audience,
                       ValidateIssuerSigningKey = true,
                       IssuerSigningKey = new SymmetricSecurityKey(jwtKeyProvider.SigningKey.ToArray()),
                       ValidateLifetime = true,
                       ClockSkew = TimeSpan.FromSeconds(30),
                       NameClaimType = JwtRegisteredClaimNames.Name,
                       RoleClaimType = NodeAuthorizationPolicies.RoleClaimType
                   };
                   options.Events = new JwtBearerEvents
                   {
                       OnMessageReceived = context =>
                       {
                           var path = context.HttpContext.Request.Path;
                           if (path.StartsWithSegments($"/{LocalApiRoutes.Prefix}", StringComparison.OrdinalIgnoreCase)
                               && path.Value?.EndsWith("/hub", StringComparison.OrdinalIgnoreCase) == true)
                           {
                               var token = context.Request.Query["access_token"].FirstOrDefault();
                               if (!string.IsNullOrWhiteSpace(token))
                               {
                                   context.Token = token;
                               }
                           }

                           return Task.CompletedTask;
                       }
                   };
               });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NodeAuthorizationPolicies.Operator,
                policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                                .RequireAuthenticatedUser()
                                .RequireRole(NodeAuthorizationPolicies.AdminRole));
        });
        builder.Services.AddAntiforgery();
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
        // Production limit is 10/min per client IP. Test environments drive many auth calls from a
        // single loopback IP (one partition), so relax the cap there to keep E2E/integration runs
        // deterministic without weakening the production control.
        var authPermitLimit = builder.Environment.IsEnvironment("Testing") ? 10_000 : 10;
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy(NodeAuthRateLimits.AuthPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = authPermitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";
                context.HttpContext.Response.Headers["Retry-After"] = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    message = "Too many auth attempts. Please try again later."
                }, cancellationToken).ConfigureAwait(false);
            };
        });
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
        builder.Services.AddSingleton<DeadLetterFlushService>();
        builder.Services.AddSingleton<IWorkerShutdownDrainService, WorkerShutdownDrainService>();
        builder.Services.AddSingleton<IOllamaModelService, OllamaModelService>();
        builder.Services.AddSingleton<ILocalChatRuntimePackageBuilder, LocalChatRuntimePackageBuilder>();
        builder.Services.AddSingleton<ILocalToolOfferProvider, LocalToolOfferProvider>();
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
        builder.Services.AddHostedService<HeartbeatBackgroundService>();
        builder.Services.AddHostedService<AutoConnectBackgroundService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<RetentionSweeperService>();
        builder.Services.AddHostedService<ToolCallCleanupService>();
        builder.Services.AddHealthChecks()
               .AddCheck<WorkerHealthCheck>("worker_health", tags: ["ready"])
               .AddCheck<OllamaHealthCheck>("ollama_health", HealthStatus.Unhealthy, ["ready"]);
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

    public static void ConfigureJsonSerializerOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();

        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
        {
            options.Converters.Add(new JsonStringEnumConverter());
        }
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

    private static string GetRateLimitPartitionKey(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }

    private sealed record ChatConnectionSettings(Uri Endpoint, string Model);
}
