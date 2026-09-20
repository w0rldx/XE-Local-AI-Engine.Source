namespace XE_Local_AI_Engine.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSwag;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.Automation.V1;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Security.DataProtection;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using LoggerExtensions = XE_Local_AI_Engine.Client.Common.Extensions.LoggerExtensions;

/// <summary>
///     Represents configure services.
/// </summary>
public static class ConfigureServices
{
    private const string ConsoleOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static void AddServices(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        // Serilog: console always on, a date-rolled file sink under the per-user data dir, and writeToProviders MUST stay true — its false default makes
        // Serilog the terminus and dead-ends every ILogger call before the OTLP exporter. See docs/wiki/11-hosting-and-deployment.md ("Logging and the Data Protection key-ring at registration time").
        var logFileDirectory = LoggerExtensions.ResolveLogFileDirectory(builder.Environment, configuration);
        _ = builder.Services.AddSerilog((serviceCollection, lc) =>
            {
                _ = lc.ReadFrom.Configuration(configuration)
                      .ReadFrom.Services(serviceCollection)
                      .Enrich.FromLogContext()
                      .WriteTo.Console(theme: ConsoleTheme.None, outputTemplate: ConsoleOutputTemplate);

                if (logFileDirectory is not null)
                {
                    _ = lc.WriteToRollingFile(logFileDirectory);
                }
            },
            writeToProviders: true);

        // Explicit, stable Data Protection key-ring: a pinned application-name discriminator and the per-user data directory, so the
        // ring survives Velopack updates and reinstalls. AddDataProtection() is idempotent, so this re-registers nothing.
        var dataProtectionRoot = configuration[DesktopBootstrap.NodeDataDirectoryKey];
        if (string.IsNullOrWhiteSpace(dataProtectionRoot))
        {
            dataProtectionRoot = builder.Environment.ContentRootPath;
        }

        var dataProtection = builder.Services.AddDataProtection()
                                    .SetApplicationName("XE-Local-AI-Engine")
                                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataProtectionRoot, "dp-keys")));

        // Encrypt the key-ring at rest: DPAPI (CurrentUser) on Windows, AES-256-GCM under an operator-secret KEK elsewhere. WRITE-side
        // only, so existing plaintext keys and payloads keep reading, and a wrong or missing secret fails closed on the GCM tag.
        var isWindows = OperatingSystem.IsWindows();
        if (isWindows)
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: false);
        }
        else
        {
            dataProtection.Services.AddSingleton<INodeDataProtectionKeyProvider, NodeDataProtectionKeyProvider>();
            dataProtection.Services.AddSingleton<NodeDataProtectionKeyRingEncryptor>();
            dataProtection.Services.AddOptions<KeyManagementOptions>()
                          .Configure<NodeDataProtectionKeyRingEncryptor>((options, encryptor) => options.XmlEncryptor = encryptor);
        }

        // Hard-fail instead of silently regenerating the ring when an at-rest key cannot be decrypted, which would orphan every stored
        // credential. Applied on BOTH schemes and deliberately OUTSIDE the branch above — inside it, Windows fails OPEN.
        _ = NodeDataProtectionKeyRingFailClosed.Decorate(dataProtection.Services,
            NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows));

        // The one wall-clock seam: every service needing "now" takes TimeProvider and calls GetUtcNow(), and DateTimeOffset.UtcNow is
        // banned (BannedSymbols.txt, RS0030). A test overrides it through TestServerWebAppFactory.ConfigureAdditionalTestServices.
        builder.Services.TryAddSingleton(TimeProvider.System);

        // The application layer (services, options, persistence, runtime) lives in XE-Local-AI-Engine.Client.Application; the host wires
        // only web-framework concerns below — FastEndpoints, auth, SignalR, rate limiting, health checks, hosted services.
        builder.AddNodeApplication(configuration);

        // Quartz scheduler runtime (persistent store + hosted service + dispatcher). Registers nothing when
        // Scheduler:Enabled is false. The QRTZ_ tables are created by the same node-chat EF migration.
        builder.AddNodeScheduler(configuration);

        // This node's INBOUND MCP server, the Streamable HTTP surface an external MCP client delegates work through. Registration is
        // unconditional, but the endpoint authenticates nobody until the operator generates a key, so opting out exposes no tool.
        builder.AddNodeMcpServer();

        // Hub-backed scheduler event publisher — supersedes the no-op default registered in AddNodeScheduler so
        // run/definition lifecycle events broadcast to connected SignalR clients (SchedulerHub mapped in Program).
        builder.Services.AddSingleton<ISchedulerEventPublisher, SchedulerEventPublisher>();

        // Ordered per-run benchmark output relay. The application buffer remains the bounded replay authority; this
        // host service only bridges its published events to the Operator-scoped benchmark hub.
        builder.Services.AddHostedService<BenchmarkRunHubEventRelay>();

        // Same split for dataset generation: the application buffer stays the bounded replay authority and this host
        // service only bridges its published events to the Operator-scoped generation hub.
        builder.Services.AddHostedService<DatasetGenerationHubEventRelay>();

        // And again for training runs: the run buffer owns replay, this only bridges to the Operator-scoped run hub.
        builder.Services.AddHostedService<TrainingRunHubEventRelay>();

        // Hub-backed GGUF download event publisher, superseding the no-op AddNodeModelFit registers so status changes push live on
        // GgufDownloadHub instead of a per-second poll. IHubContext is singleton-safe, so the singleton coordinator can resolve it.
        builder.Services.AddSingleton<IGgufDownloadEventPublisher, GgufDownloadEventPublisher>();

        // Hub-backed in-app source-build event publisher, superseding the no-op the provider registers so build phase and log lines
        // push live on LlamaCppSourceBuildHub. IHubContext is singleton-safe.
        builder.Services.AddSingleton<ILlamaCppSourceBuildEventPublisher, LlamaCppSourceBuildEventPublisher>();

        // Hub-backed runtime-acquisition publisher: a plain AddSingleton, so it wins over the provider's TryAdd, turning the silent GPU
        // probe / download / verify / extract sequence into live pushes. IHubContext is singleton-safe for the status registry.
        builder.Services.AddSingleton<IRuntimeAcquisitionEventPublisher, RuntimeAcquisitionEventPublisher>();

        // Hub-backed knowledge-base indexing notifier, superseding the no-op AddNodeKnowledgeBase registers so document status changes
        // push live on KnowledgeBaseHub. IHubContext is singleton-safe, so the scoped ingestion service can resolve this singleton.
        builder.Services.AddSingleton<IKnowledgeIndexingNotifier, KnowledgeIndexingNotifier>();

        // Hub-backed image-job event publisher, superseding the no-op AddNodeImages registers so coarse status transitions push live on
        // ImageJobHub. IHubContext is singleton-safe, so the singleton image-job coordinator can resolve it.
        builder.Services.AddSingleton<IImageJobEventPublisher, ImageJobEventPublisher>();
        builder.Services.AddSingleton<IStableDiffusionCppSourceBuildEventPublisher, StableDiffusionCppSourceBuildEventPublisher>();

        // Hub-backed training-runtime event publisher: a plain AddSingleton, so it wins over the provider's TryAdd, pushing uv install
        // phase and log lines live on TrainingRuntimeHub. IHubContext is singleton-safe.
        builder.Services.AddSingleton<ITrainingRuntimeEventPublisher, TrainingRuntimeEventPublisher>();

        // Hub-backed work-session event publisher — supersedes the no-op the work-session module registers with
        // TryAddSingleton, so a change committed by the supervisor or by a state tool reaches the session view live.
        builder.Services.AddSingleton<IWorkSessionEventPublisher, WorkSessionEventPublisher>();

        // Same posture for development workflows: the hub-backed publisher supersedes the no-op the module registers
        // with TryAddSingleton, so every committed run change reaches an open run view live.
        builder.Services.AddSingleton<IDevWorkflowEventPublisher, DevWorkflowEventPublisher>();

        // And for graph workflows, whose publishing store decorator announces every committed run mutation.
        builder.Services.AddSingleton<IGraphWorkflowEventPublisher, GraphWorkflowEventPublisher>();

        // Transcription: the hub-backed publisher supersedes the no-op AddNodeTranscription registers with
        // TryAddSingleton, so every committed live segment, partial and end reason reaches an open session view.
        builder.Services.AddSingleton<ITranscriptionEventPublisher, TranscriptionEventPublisher>();

        // External apps: the hub-backed publisher supersedes the no-op AddNodeExternalApps registers with TryAddSingleton, so a pull, a
        // daemon-watcher state change and the boot reconciler's adoptions all reach an open instance view live.
        builder.Services.AddSingleton<IExternalAppEventPublisher, ExternalAppEventPublisher>();

        // Composes the run-detail and node-detail read shapes, which need the pinned graph and the agent names beside
        // the rows. Scoped, because the stores it reads are.
        builder.Services.AddScoped<DevWorkflowRunComposer>();

        // Development ships enabled. Keep the no-op publisher only when the administrator explicitly disables it.
        var developmentEnabled = configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true);
        if (developmentEnabled)
        {
            builder.Services.AddSingleton<IDevelopmentAttemptLiveEventPublisher, DevelopmentAttemptLiveEventPublisher>();
        }

        // Error handling - the order of the exception handlers is important: specific handlers first, family-specific
        // wire contracts next, and DefaultExceptionHandler last as the catch-all 500.
        builder.Services
               .AddExceptionHandler<ConflictExceptionHandler>()
               .AddExceptionHandler<DomainValidationExceptionHandler>()
               .AddExceptionHandler<DevelopmentConflictExceptionHandler>()
               .AddExceptionHandler<SelectedFolderExceptionHandler>()
               .AddExceptionHandler<TrainingExceptionHandler>()
               .AddExceptionHandler<BenchmarkExceptionHandler>()
               .AddExceptionHandler<GgufImportExceptionHandler>()
               .AddExceptionHandler<GgufDownloadExceptionHandler>()
               .AddExceptionHandler<WorkSessionNotFoundExceptionHandler>()
               .AddExceptionHandler<DevWorkflowNotFoundExceptionHandler>()
               .AddExceptionHandler<DevelopmentNotFoundExceptionHandler>()
               .AddExceptionHandler<GraphWorkflowNotFoundExceptionHandler>()
               .AddExceptionHandler<ExternalAppNotFoundExceptionHandler>()
               .AddExceptionHandler<ContainerRuntimeUnavailableExceptionHandler>()
               .AddExceptionHandler<RequestBodyTooLargeExceptionHandler>()
               .AddExceptionHandler<DefaultExceptionHandler>();
        builder.Services.AddProblemDetails();

        builder.Services.ConfigureHttpJsonOptions(options => ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddFastEndpoints(options =>
        {
            options.DisableAutoDiscovery = true;
            options.Assemblies = [typeof(ConfigureServices).Assembly];

            // Development Mode's endpoints are kept out of DISCOVERY, not out of routing, when the feature is off: the routing filter in
            // UseFastEndpoints is too late. See docs/wiki/09-api-and-hubs.md ("Dev-only surfaces").
            options.Filter = type => developmentEnabled || !typeof(IDevelopmentEndpoint).IsAssignableFrom(type);
        });
        builder.Services.AddSignalR(options =>
        {
            options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            // Transport ceiling for ONE hub-invocation payload, kept deliberately above Security:MaxMessageSizeKb so an oversized paste is
            // refused by that app-level check with a legible message rather than by SignalR's opaque frame-size error. Raise the two together.
            options.MaximumReceiveMessageSize = 512 * 1024;
            options.StreamBufferCapacity = 1;
        });
        // Seed the FastEndpoints serializer global HERE, at registration time, or the OpenAPI generator snapshots a PascalCase copy. The global is
        // process-wide, so a read-only one has been served with already. See docs/wiki/09-api-and-hubs.md ("Why the FastEndpoints serializer global is seeded at registration time").
        var fastEndpointsSerializerOptions = new Config().Serializer.Options;
        if (!fastEndpointsSerializerOptions.IsReadOnly)
        {
            ConfigureJsonSerializerOptions(fastEndpointsSerializerOptions);
        }

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

                // NJsonSchema emits CLR member names for string enums; honour [JsonStringEnumMemberName] so the OpenAPI enum values
                // match the wire format ("running", not "Running"). Without this, generated client validators reject valid responses.
                settings.SchemaSettings.SchemaProcessors.Add(new JsonStringEnumMemberNameSchemaProcessor());
                settings.OperationProcessors.Add(new McpServerApiKeyOpenApiOperationProcessor());
                settings.DocumentProcessors.Add(new DevelopmentOpenApiDocumentProcessor());
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
               .AddJwtBearer()
               // Second scheme, applied ONLY by the McpServer policy on the inbound MCP endpoint. JWT bearer stays the
               // default scheme, so every existing endpoint and hub keeps its current behavior unchanged.
               .AddScheme<AuthenticationSchemeOptions, McpApiKeyAuthenticationHandler>(McpApiKeyAuthenticationHandler.SchemeName, configureOptions: null)
               // Third scheme, applied ONLY by the LocalModelProxy policy: independent of JWT bearer and the MCP key, so a tool that
               // consumes only the raw model gains neither the operator's admin reach nor the MCP client's agent-tool reach.
               .AddScheme<AuthenticationSchemeOptions, LocalModelProxyApiKeyAuthenticationHandler>(LocalModelProxyApiKeyAuthenticationHandler.SchemeName, configureOptions: null)
               // Fourth scheme, applied ONLY by the IntegrationApi policy on the hand-mapped integration routes: independent of all three
               // above, so an integrator gains neither the operator's admin reach, the MCP client's tool reach, nor the proxy's raw model.
               .AddScheme<AuthenticationSchemeOptions, IntegrationApiKeyAuthenticationHandler>(IntegrationApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
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
                       },
                       // Stateless JWTs carry no revocation state, so enforce the user's current Identity security stamp here: a password
                       // reset rotates it and must invalidate every token minted before the change. One indexed lookup per request.
                       OnTokenValidated = static async context =>
                       {
                           var userId = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                           if (string.IsNullOrEmpty(userId))
                           {
                               return;
                           }

                           // Fail CLOSED when the token carries no stamp: every token minted for a persisted user binds one, so an unstamped
                           // but validly-signed token is a legacy token that must not outlive a reset, or a forgery. Never a bypass.
                           var tokenStamp = context.Principal?.FindFirst(NodeAuthorizationPolicies.SecurityStampClaimType)?.Value;
                           if (string.IsNullOrEmpty(tokenStamp))
                           {
                               context.Fail("Access token is missing its security stamp.");
                               return;
                           }

                           var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<NodeUser>>();
                           var user = await userManager.FindByIdAsync(userId);

                           // No persisted row for the subject: preserve the base stateless-JWT posture, where the token authenticates and each
                           // endpoint resolves the user. The stamp is a revocation signal, not an existence check.
                           if (user is not null
                               && !string.Equals(await userManager.GetSecurityStampAsync(user), tokenStamp, StringComparison.Ordinal))
                           {
                               context.Fail("Access token security stamp is stale.");
                           }
                       }
                   };
               });
        builder.Services.AddAuthorization(options =>
        {
            // Deny by default at the framework layer, behind the FastEndpoints Configurator, and deliberately weaker than Operator: the
            // second layer, not a replacement. See docs/wiki/09-api-and-hubs.md ("Security middleware & auth ordering").
            options.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                                     .RequireAuthenticatedUser()
                                     .Build();

            options.AddPolicy(NodeAuthorizationPolicies.Operator,
                policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                                .RequireAuthenticatedUser()
                                .RequireRole(NodeAuthorizationPolicies.AdminRole));

            // The inbound MCP endpoint accepts ONLY the MCP API key scheme, never the operator's JWT: listing one scheme stops a browser
            // session or a stolen token from driving it, and an MCP client from presenting as the operator. The key IS the authorization.
            options.AddPolicy(NodeAuthorizationPolicies.McpServer,
                policy => policy.AddAuthenticationSchemes(McpApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser());

            options.AddPolicy(NodeAuthorizationPolicies.McpAgentic,
                policy => policy.AddAuthenticationSchemes(McpApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser()
                                .RequireClaim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope));

            // The inbound model proxy accepts ONLY the model-proxy API key scheme, never the operator's JWT and never the MCP key. One
            // scheme, no role: the key IS the authorization, so none of those can drive the raw-model surface.
            options.AddPolicy(NodeAuthorizationPolicies.LocalModelProxy,
                policy => policy.AddAuthenticationSchemes(LocalModelProxyApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser());

            // The external integration API accepts ONLY the integration key scheme, with no role and no claim requirement: every finer
            // decision — which triggers this key may invoke, which rows it owns — reads the key row afresh, never a minted claim.
            options.AddPolicy(NodeAuthorizationPolicies.IntegrationApi,
                policy => policy.AddAuthenticationSchemes(IntegrationApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser());
        });
        builder.Services.AddAntiforgery();

        // Inbound model proxy forwarder + its dedicated forwarding client, whose deadline contract (infinite timeout,
        // no resilience pipeline) lives with the registration.
        builder.Services.AddNodeModelProxy();

        // The external integration API's hand-mapped handler, scoped like the proxy forwarder for the same reason: its
        // collaborators are scoped stores and application services.
        builder.Services.AddScoped<IntegrationApiHandler>();

        // Singleton: it owns the process-wide open-stream semaphore, and a scoped one would give every request its own
        // cap, which is no cap at all.
        builder.Services.AddSingleton<IntegrationSseWriter>();

        // Production is 10/min per client IP; Testing drives many auth calls from one loopback partition, so it is relaxed there without weakening production. Every
        // permit limit is computed HERE, outside the AddRateLimiter lambda, so its closure captures ints, never `builder`. Why: docs/wiki/11-hosting-and-deployment.md ("Registration-time closures").
        var isTestingEnvironment = builder.Environment.IsEnvironment("Testing");
        var authPermitLimit = isTestingEnvironment ? 10_000 : 10;
        var mcpPermitLimit = isTestingEnvironment ? 100_000 : 120;
        var proxyPermitLimit = isTestingEnvironment ? 100_000 : 6_000;

        // The integration API's COARSE PER-IP ceiling, deliberately NOT IntegrationOptions.RateLimitPerMinute: that is the PER-PRINCIPAL budget,
        // spent by IntegrationPrincipalRateLimiter where a principal exists to partition on. From configuration, computed outside the lambda.
        var integrationPermitLimit = isTestingEnvironment
            ? 100_000
            : builder.Configuration.GetValue($"{IntegrationOptions.Section}:{nameof(IntegrationOptions.IpRateLimitPerMinute)}", defaultValue: 6_000);

        // Registered through a factory so the CONTAINER disposes it: an undisposed PartitionedRateLimiter roots its
        // replenishment timer and, through it, the whole host graph — the leak documented a few lines above.
        builder.Services.AddSingleton(_ => new IntegrationPrincipalRateLimiter(
            builder.Configuration.GetValue($"{IntegrationOptions.Section}:{nameof(IntegrationOptions.RateLimitPerMinute)}", defaultValue: 600)));

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

            // Inbound MCP. The 256-bit key is what makes guessing infeasible, not this: the cap bounds the attempt rate and turns a runaway client into a 429 rather than unbounded load.
            // Sized for real MCP traffic — a connect does tools/list, then a call per delegated task — which is why it is well above the auth endpoints'. Testing is relaxed like AuthPolicy.
            options.AddPolicy(NodeAuthRateLimits.McpPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = mcpPermitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Inbound model proxy. NOT a key-guessing defense — a 256-bit key is uncrackable at any cap — so unlike a login throttle it must not shape legitimate inference traffic:
            // one client doing RAG indexing issues far more than the MCP surface does, so this bounds only a runaway client. Per-model compute is bounded by the loaded-cap and leases.
            options.AddPolicy(NodeAuthRateLimits.LocalModelProxyPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = proxyPermitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // External integration API, on the same shared IP partition function as the three above: this middleware runs BEFORE UseAuthentication, so no
            // claim exists to partition on. Per-principal fairness is IntegrationPrincipalRateLimiter plus the admission cap inside the accept transaction.
            options.AddPolicy(NodeAuthRateLimits.IntegrationApiPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = integrationPermitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json";
                context.HttpContext.Response.Headers["Retry-After"] = "60";

                // OnRejected is SHARED by every policy above, so the body must name the policy that actually rejected: only AuthPolicy throttles
                // sign-in, and telling an integrator it made "too many auth attempts" sends it to rotate a credential that was never the problem.
                var policyName = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    message = string.Equals(policyName, NodeAuthRateLimits.AuthPolicy, StringComparison.Ordinal)
                        ? "Too many auth attempts. Please try again later."
                        : "Too many requests. Please try again later."
                }, cancellationToken);
            };
        });

        // Seeds the enabled on-demand (Manual) model-recommendation-check schedule so the React "Refresh now" button works out of the box. AFTER
        // AddNodeScheduler, so the factory and job store exist when the seeder's StartAsync calls IScheduledJobManagementService.
        builder.Services.AddHostedService<ModelRecommendationScheduleSeeder>();
        // Seeds the node-local "Default Assistant" agent definition (mode-off persona) so every send resolves through a
        // real, uniformly-selectable definition. Idempotent by slug and self-healing across boots.
        builder.Services.AddHostedService<DefaultAgentSeeder>();
        // Seeds the node-local "Coder (read-only)" agent definition, with read/list/search project access, so the profile is selectable out of
        // the box. Idempotent by slug and self-healing across boots, like the Default Assistant seeder above.
        builder.Services.AddHostedService<CoderAgentSeeder>();
        // Seeds the node-local "Mathematician" agent definition, the one persona opting into the sandboxed run_python compute tool, which is profile-opt-in only and unreachable without it.
        // Idempotent by slug and registered unconditionally, but the seeder skips when Compute:Enabled is false, so the gate reads the validated ComputeOptions rather than raw configuration.
        builder.Services.AddHostedService<MathematicianAgentSeeder>();
        builder.Services.AddHostedService<ToolCallCleanupService>();
        // Encrypts legacy plaintext message rows (content + metadata_json) into the read-both at-rest envelope. Batched, transactional, resumable and
        // idempotent. Before the title backfill so titles re-derive from migrated rows where possible; both are read-both, so order is not required.
        builder.Services.AddHostedService<NodeChatContentEncryptionBackfillService>();
        // One-time L2-normalization of legacy chunk vectors so cosine search scores by dot product: batched, transactional, resumable, idempotent, marker-tracked in chat_maintenance_state.
        // Client host only, never the shared KB module, so it cannot race a test host's fixtures: search stays on the cosine path until it completes, then flips IKnowledgeVectorNormalizationState.
        builder.Services.AddHostedService<KnowledgeVectorNormalizationBackfillService>();
        // Re-derives and re-encrypts conversation titles that were NULLed by the EncryptConversationTitle migration
        // (migrations cannot access the node key; this service runs once per startup and is idempotent).
        builder.Services.AddHostedService<NodeChatTitleEncryptionBackfillService>();
        // Upgrade backfill for the external-access profile, stamping "recommended" on a node that completed setup but predates the profile. Idempotent,
        // node-local, not desktop-gated, and in StartAsync so the decision is durable before Kestrel accepts a request.
        builder.Services.AddHostedService<ExternalAccessProfileBackfillService>();
        // Upgrade backfill for the navigation mode, in StartAsync for the same reason. Its discriminator is the STORED external-access profile, not the
        // service above's output, so it does not depend on running after it.
        builder.Services.AddHostedService<UiModeBackfillService>();
        // FRR-2 upgrade backfill: maps an Ollama model pulled on an EARLIER build, which wrote no provider-map row, to the ollama provider, so the flipped
        // llamacpp default does not silently re-route it. Idempotent, offline-tolerant and not desktop-gated.
        builder.Services.AddHostedService<OllamaProviderMapBackfillService>();
        // Desktop-only first-run model provisioning: installs and selects a small node-local GGUF through the bundled llama.cpp runtime, so a fresh
        // double-click install can chat out of the box. Offline-tolerant; headless/Aspire/CI never auto-download (off-flag invariant).
        builder.Services.AddHostedService<FirstRunModelProvisioningService>();
        // Readiness = essential node-local persistence: a dead or unwritable SQLite store must flip /health/ready.
        builder.Services.AddHealthChecks()
               .AddCheck<NodeSqliteHealthCheck>("node_sqlite", tags: ["ready"]);
    }

    public static void ConfigureJsonSerializerOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Stated rather than inherited: a runtime options instance already carries camelCase from JsonSerializerDefaults.Web, but the FastEndpoints
        // serializer global starts as a bare PascalCase JsonSerializerOptions and the OpenAPI generator snapshots THAT.
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();

        if (!options.Converters.OfType<SlashCommandActionTypeDtoJsonConverter>().Any())
        {
            options.Converters.Insert(index: 0, new SlashCommandActionTypeDtoJsonConverter());
        }

        if (!options.Converters.OfType<LocalModelOriginJsonConverter>().Any())
        {
            options.Converters.Insert(index: 0, new LocalModelOriginJsonConverter());
        }

        if (!options.Converters.Any(static converter => converter is JsonStringEnumConverter<McpServerApiKeyScope>))
        {
            options.Converters.Insert(index: 0,
                new JsonStringEnumConverter<McpServerApiKeyScope>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        }

        if (!options.Converters.OfType<JsonStringEnumConverter>().Any())
        {
            options.Converters.Add(new JsonStringEnumConverter());
        }
    }

    private static string GetRateLimitPartitionKey(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }
}
