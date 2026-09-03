namespace XE_Local_AI_Engine.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
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
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Security.DataProtection;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Client.Services.Scheduler;
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
        // Serilog. Console is always on; a date-rolled file sink is added under the per-user data dir (same resolution as
        // the Data Protection key-ring below) so desktop/dev logs survive the console window closing and a tester bug
        // report has on-disk history. Disabled in Testing (many parallel hosts would contend for the exclusive file).
        //
        // writeToProviders MUST stay true. It defaults to false, which makes Serilog the terminus of the logging
        // pipeline: events reach Serilog's own sinks and no other registered ILoggerProvider. The OpenTelemetry logger
        // provider that ConfigureOpenTelemetry registers (ServiceDefaults/Extensions.cs) is one of those, so with the
        // default every ILogger call dead-ends before the OTLP log exporter and the Aspire dashboard shows zero
        // structured logs while traces/metrics still flow (those bypass ILoggerFactory entirely). Program.cs calls
        // Logging.ClearProviders() before AddServiceDefaults, so OpenTelemetry is the only other provider in the chain
        // and forwarding cannot resurrect a duplicate console logger.
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

        // Explicit, stable Data Protection key-ring. The framework already auto-registers Data Protection (so the
        // encrypted token stores — CloudCredentialStore, CodexTokenStore and HfTokenStore — plus the auth
        // TokenStore — are protected today); this is DEFENSIVE stability hardening, not a confidentiality fix. It pins
        // a STABLE application-name discriminator so the key-ring never shifts between Velopack updates, and persists
        // the keys under the SAME per-user data directory the rest of the node state uses (the NodeData:Directory key
        // DesktopBootstrap layers in for desktop mode; ContentRoot otherwise — preserving the off-flag byte-behavior
        // invariant), so the key-ring is co-located with node.sqlite/node.key and survives reinstalls instead of
        // landing in the volatile default location. AddDataProtection() is idempotent (TryAdd-based), so this does not
        // double-register or change the IDataProtectionProvider existing consumers resolve.
        var dataProtectionRoot = configuration[DesktopBootstrap.NodeDataDirectoryKey];
        if (string.IsNullOrWhiteSpace(dataProtectionRoot))
        {
            dataProtectionRoot = builder.Environment.ContentRootPath;
        }

        var dataProtection = builder.Services.AddDataProtection()
                                    .SetApplicationName("XE-Local-AI-Engine")
                                    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataProtectionRoot, "dp-keys")));

        // Encrypt the key-ring at rest. On Windows, DPAPI (CurrentUser) — unchanged. On non-Windows (BE-02), wrap NEW
        // key-ring elements with AES-256-GCM under a KEK derived from the node operator secret (HKDF-SHA256, distinct
        // info string), so the key-ring inherits the same env/secret-file protection as node.sqlite instead of sitting
        // in plaintext beside the ciphertext it unlocks. The encryptor is WRITE-side only: existing plaintext keys and
        // existing IDataProtector payloads keep reading, because Data Protection reads each key in whatever form it was
        // written (no proactive re-wrap of the current active key — deferred, and safer that way). A wrong/missing
        // operator secret fails closed (the AES-GCM tag will not authenticate). node.sqlite is PLAIN SQLite with
        // application-level COLUMN encryption (not SQLCipher/whole-file), so a wrong secret does NOT reliably fail
        // startup on its own — the loud backstop is the fail-closed key resolver wired below, which refuses to
        // regenerate the ring when an encrypted key cannot be decrypted.
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

        // Hard-fail instead of silently regenerating the ring when an at-rest key cannot be decrypted. Data Protection's
        // DefaultKeyResolver swallows a per-key decrypt failure and, finding no usable default, generates a fresh key —
        // orphaning every stored credential/token.
        //
        // Applied on BOTH schemes, and deliberately OUTSIDE the branch above. It used to sit inside the non-Windows
        // arm, which left Windows failing OPEN: an unreadable DPAPI ring quietly minted a new key and made every *.enc
        // credential (HF token, GitHub auth, cloud creds) undecryptable with no hard failure and no log line. The
        // decoration wraps the RESOLVER and is orthogonal to how keys are encrypted, so the only thing that has to
        // differ is which failure counts as a ring failure and what the operator can do about it.
        _ = NodeDataProtectionKeyRingFailClosed.Decorate(dataProtection.Services,
            NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows));

        // Application layer (services, options, persistence, runtime) lives in the
        // XE-Local-AI-Engine.Client.Application class library. The host only wires web-framework
        // concerns below (FastEndpoints, auth, SignalR, rate limiting, health checks, hosted services).
        builder.AddNodeApplication(configuration);

        // Quartz scheduler runtime (persistent store + hosted service + dispatcher). Registers nothing when
        // Scheduler:Enabled is false. The QRTZ_ tables are created by the same node-chat EF migration.
        builder.AddNodeScheduler(configuration);

        // This node's INBOUND MCP server: the Streamable HTTP surface an external MCP client connects to in order to
        // delegate a task to the local model. Registration is unconditional, but the endpoint authenticates nobody
        // until the operator generates a key, so a node that never opts in exposes no reachable tool.
        builder.AddNodeMcpServer();

        // Hub-backed scheduler event publisher — supersedes the no-op default registered in AddNodeScheduler so
        // run/definition lifecycle events broadcast to connected SignalR clients (SchedulerHub mapped in Program).
        builder.Services.AddSingleton<ISchedulerEventPublisher, SchedulerEventPublisher>();

        // Hub-backed preview-workflow event publisher — supersedes the no-op default registered in AddNodePreviewWorkflows
        // so run/node lifecycle events broadcast to the connected operator (PreviewWorkflowHub mapped in Program).
        builder.Services.AddSingleton<IPreviewWorkflowEventPublisher, PreviewWorkflowEventPublisher>();

        // Ordered per-run benchmark output relay. The application buffer remains the bounded replay authority; this
        // host service only bridges its published events to the Operator-scoped benchmark hub.
        builder.Services.AddHostedService<BenchmarkRunHubEventRelay>();

        // Same split for dataset generation: the application buffer stays the bounded replay authority and this host
        // service only bridges its published events to the Operator-scoped generation hub.
        builder.Services.AddHostedService<DatasetGenerationHubEventRelay>();

        // And again for training runs: the run buffer owns replay, this only bridges to the Operator-scoped run hub.
        builder.Services.AddHostedService<TrainingRunHubEventRelay>();

        // Hub-backed GGUF download event publisher — supersedes the no-op default registered in AddNodeModelFit so
        // download status changes push live to operator clients (GgufDownloadHub mapped in Program), replacing the
        // per-second downloads poll. IHubContext is singleton-safe, so the singleton coordinator can resolve it.
        builder.Services.AddSingleton<IGgufDownloadEventPublisher, GgufDownloadEventPublisher>();

        // Hub-backed in-app CUDA build event publisher — supersedes the no-op default the provider registers so build
        // phase + log lines push live to operator clients (CudaBuildHub mapped in Program). IHubContext is singleton-safe.
        builder.Services.AddSingleton<ICudaBuildEventPublisher, CudaBuildEventPublisher>();
        builder.Services.AddSingleton<ILlamaCppSourceBuildEventPublisher, LlamaCppSourceBuildEventPublisher>();

        // Hub-backed first-run runtime-acquisition event publisher — supersedes the no-op default the provider registers
        // (a plain AddSingleton, so it wins over that TryAdd) and turns the previously-silent GPU probe / archive download
        // / verify / extract sequence into live pushes on RuntimeAcquisitionHub. IHubContext is singleton-safe, so the
        // singleton status registry can resolve it.
        builder.Services.AddSingleton<IRuntimeAcquisitionEventPublisher, RuntimeAcquisitionEventPublisher>();

        // Hub-backed knowledge-base indexing notifier — supersedes the no-op default registered in AddNodeKnowledgeBase so
        // document status changes push live to operator clients (KnowledgeBaseHub mapped in Program). IHubContext is
        // singleton-safe, so the scoped ingestion service can resolve this singleton.
        builder.Services.AddSingleton<IKnowledgeIndexingNotifier, KnowledgeIndexingNotifier>();

        // Hub-backed image-job event publisher — supersedes the no-op default registered in AddNodeImages so coarse job
        // status transitions push live to operator clients (ImageJobHub mapped in Program). IHubContext is singleton-safe,
        // so the singleton image-job coordinator can resolve it.
        builder.Services.AddSingleton<IImageJobEventPublisher, ImageJobEventPublisher>();
        builder.Services.AddSingleton<IStableDiffusionCppSourceBuildEventPublisher, StableDiffusionCppSourceBuildEventPublisher>();

        // Hub-backed training-runtime event publisher — supersedes the no-op default the provider registers (a plain
        // AddSingleton, so it wins over that TryAdd) so uv install phase + log lines push live to operator clients
        // (TrainingRuntimeHub mapped in Program). IHubContext is singleton-safe.
        builder.Services.AddSingleton<ITrainingRuntimeEventPublisher, TrainingRuntimeEventPublisher>();

        // Hub-backed work-session event publisher — supersedes the no-op the work-session module registers with
        // TryAddSingleton, so a change committed by the supervisor or by a state tool reaches the session view live.
        builder.Services.AddSingleton<IWorkSessionEventPublisher, WorkSessionEventPublisher>();

        // Same posture for development workflows: the hub-backed publisher supersedes the no-op the module registers
        // with TryAddSingleton, so every committed run change reaches an open run view live.
        builder.Services.AddSingleton<IDevWorkflowEventPublisher, DevWorkflowEventPublisher>();

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
               .AddExceptionHandler<TrainingExceptionHandler>()
               .AddExceptionHandler<BenchmarkExceptionHandler>()
               .AddExceptionHandler<WorkSessionNotFoundExceptionHandler>()
               .AddExceptionHandler<DevWorkflowNotFoundExceptionHandler>()
               .AddExceptionHandler<DefaultExceptionHandler>();
        builder.Services.AddProblemDetails();

        builder.Services.ConfigureHttpJsonOptions(options => ConfigureJsonSerializerOptions(options.SerializerOptions));
        builder.Services.AddFastEndpoints(options =>
        {
            options.DisableAutoDiscovery = true;
            options.Assemblies = [typeof(ConfigureServices).Assembly];

            // Development Mode's endpoints are kept out of DISCOVERY — not out of routing — when the feature is off.
            // The routing filter in UseFastEndpoints is too late: FastEndpoints instantiates every discovered endpoint
            // at startup before evaluating that filter, and AddNodeDevelopment registers the services those endpoints
            // take through their constructors only when the feature is on, so a discovered-but-unrouted Development
            // endpoint would fail the node's boot. GetDevelopmentCapabilityEndpoint deliberately does not carry the
            // marker: it stays discoverable and answers the disabled state. The 404 the other routes give while the
            // feature is off still comes from the request-path middleware in Program, which runs before authentication
            // and so is unaffected by whether the route exists.
            options.Filter = type => developmentEnabled || !typeof(IDevelopmentEndpoint).IsAssignableFrom(type);
        });
        builder.Services.AddSignalR(options =>
        {
            options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
            options.HandshakeTimeout = TimeSpan.FromSeconds(15);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            // Transport ceiling for ONE hub-invocation payload (a SendMessage envelope: content plus ids, model name,
            // selected-path map, attachment ids). Kept deliberately above Security:MaxMessageSizeKb (256 KB of content
            // by default) so an oversized paste is rejected by that app-level check with a legible message, rather than
            // by SignalR tearing the connection down with an opaque frame-size error. Raise the two together.
            options.MaximumReceiveMessageSize = 512 * 1024;
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

                // NJsonSchema emits CLR member names for string enums; honor [JsonStringEnumMemberName]
                // so the OpenAPI enum values match the wire format (e.g. host-agent runtime-status enums
                // serialize "running"/"managed", not "Running"/"Managed"). Without this, generated client
                // validators reject valid responses.
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
               // Third scheme, applied ONLY by the LocalModelProxy policy on the inbound OpenAI-compatible model proxy.
               // Independent of both JWT bearer and the MCP key so an external tool that consumes only the raw model
               // never gains the operator's admin reach nor the MCP client's agent-tool reach.
               .AddScheme<AuthenticationSchemeOptions, LocalModelProxyApiKeyAuthenticationHandler>(LocalModelProxyApiKeyAuthenticationHandler.SchemeName, configureOptions: null)
               // Fourth scheme, applied ONLY by the IntegrationApi policy on the hand-mapped external integration
               // routes. Independent of all three above so an integrator gains neither the operator's admin reach, the
               // MCP client's tool reach, nor the proxy client's raw-model reach.
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
                       // Stateless JWTs carry no revocation state, so enforce the user's current ASP.NET Identity security
                       // stamp here: password resets and changes rotate the stamp, which must immediately invalidate every
                       // access token minted before the change rather than letting it live out its lifetime. One indexed
                       // lookup per authenticated request — negligible for a single-operator local node.
                       OnTokenValidated = static async context =>
                       {
                           var userId = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
                           if (string.IsNullOrEmpty(userId))
                           {
                               return;
                           }

                           // Fail CLOSED when the token carries no stamp: every access token this version mints for a
                           // persisted user binds one, so an unstamped-but-validly-signed token is either a pre-upgrade
                           // (legacy) token that must not outlive a password reset, or a forgery that already implies
                           // signing-key compromise. Reject it either way rather than leaving a stamp-check bypass.
                           var tokenStamp = context.Principal?.FindFirst(NodeAuthorizationPolicies.SecurityStampClaimType)?.Value;
                           if (string.IsNullOrEmpty(tokenStamp))
                           {
                               context.Fail("Access token is missing its security stamp.");
                               return;
                           }

                           var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<NodeUser>>();
                           var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);

                           // No persisted row for the subject: preserve the base stateless-JWT posture (the token
                           // authenticates and each endpoint resolves the user itself). The stamp is a revocation signal
                           // for existing users, not an existence check — the single operator always exists after setup.
                           if (user is not null
                               && !string.Equals(await userManager.GetSecurityStampAsync(user).ConfigureAwait(false), tokenStamp, StringComparison.Ordinal))
                           {
                               context.Fail("Access token security stamp is stale.");
                           }
                       }
                   };
               });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(NodeAuthorizationPolicies.Operator,
                policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                                .RequireAuthenticatedUser()
                                .RequireRole(NodeAuthorizationPolicies.AdminRole));

            // The inbound MCP endpoint accepts ONLY the MCP API key scheme — never the operator's JWT. Listing just the
            // one scheme is what stops a browser session (or a stolen operator token) from driving the MCP surface, and
            // stops an MCP client from ever presenting as the operator. No role requirement: the key IS the authorization.
            options.AddPolicy(NodeAuthorizationPolicies.McpServer,
                policy => policy.AddAuthenticationSchemes(McpApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser());

            options.AddPolicy(NodeAuthorizationPolicies.McpAgentic,
                policy => policy.AddAuthenticationSchemes(McpApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser()
                                .RequireClaim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope));

            // The inbound model proxy accepts ONLY the model-proxy API key scheme — never the operator's JWT and never
            // the MCP key. One scheme, no role: the key IS the authorization, and a browser session, a stolen operator
            // token, or the MCP client can none of them drive the raw-model surface.
            options.AddPolicy(NodeAuthorizationPolicies.LocalModelProxy,
                policy => policy.AddAuthenticationSchemes(LocalModelProxyApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser());

            // The external integration API accepts ONLY the integration key scheme. One scheme, no role and no claim
            // requirement: the key IS the authorization, and every finer-grained decision (which triggers this key may
            // invoke, which rows this principal owns) is made against the freshly re-read key row rather than against a
            // claim minted at authentication time.
            options.AddPolicy(NodeAuthorizationPolicies.IntegrationApi,
                policy => policy.AddAuthenticationSchemes(IntegrationApiKeyAuthenticationHandler.SchemeName)
                                .RequireAuthenticatedUser());
        });
        builder.Services.AddAntiforgery();

        // Inbound model proxy forwarder + its dedicated forwarding client. Scoped: it resolves the per-request GGUF
        // catalog and streams one response. The client has an INFINITE timeout because a long generation must not be
        // severed by a client-side timeout — the caller's disconnect (request-abort) is the cancellation signal instead.
        builder.Services.AddScoped<LocalModelProxyForwarder>();

        // The external integration API's hand-mapped handler, scoped like the proxy forwarder for the same reason: its
        // collaborators are scoped stores and application services.
        builder.Services.AddScoped<IntegrationApiHandler>();

        // Singleton: it owns the process-wide open-stream semaphore, and a scoped one would give every request its own
        // cap, which is no cap at all.
        builder.Services.AddSingleton<IntegrationSseWriter>();
        builder.Services.AddHttpClient(LocalModelProxyForwarder.HttpClientName)
               .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan);

        // Production limit is 10/min per client IP. Test environments drive many auth calls from a
        // single loopback IP (one partition), so relax the cap there to keep E2E/integration runs
        // deterministic without weakening the production control.
        //
        // All three permit limits are computed HERE, outside the AddRateLimiter lambda, so its closure captures three
        // ints and never `builder`. This is load-bearing for test hosts: the rate-limiting middleware's partitioned
        // limiter runs a replenishment timer that is never disposed with the host, and a closure over `builder` let
        // that immortal timer root the builder -> ServiceCollection -> the entire disposed host graph (measured
        // ~20 MB per test host; gcroot evidence in docs/agent-knowledge.md §1).
        var isTestingEnvironment = builder.Environment.IsEnvironment("Testing");
        var authPermitLimit = isTestingEnvironment ? 10_000 : 10;
        var mcpPermitLimit = isTestingEnvironment ? 100_000 : 120;
        var proxyPermitLimit = isTestingEnvironment ? 100_000 : 6_000;

        // The external integration API's COARSE PER-IP CEILING — 6,000/min, the proxy's number for the proxy's reason.
        // Deliberately NOT IntegrationOptions.RateLimitPerMinute (600): that is the PER-PRINCIPAL budget and it is
        // spent by IntegrationPrincipalRateLimiter inside the handler, where a principal exists to partition on. Read
        // from configuration so a test host can lower it; computed here, outside the lambda, like the three above.
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

            // Inbound MCP. The key is 256 bits, so this is not what makes guessing infeasible — it bounds the attempt
            // rate so a local process cannot grind at it, and it turns a runaway/misconfigured client into a 429 rather
            // than an unbounded load on the node. Sized for real MCP traffic (a connect does tools/list, then a call per
            // delegated task), which is why it is 120/min rather than the auth endpoints' 10/min. Testing gets the same
            // relaxed treatment as AuthPolicy so integration/E2E runs from one loopback partition stay deterministic.
            options.AddPolicy(NodeAuthRateLimits.McpPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = mcpPermitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // Inbound model proxy. This is NOT a key-guessing defense — a 256-bit key is uncrackable no matter the cap —
            // so unlike a login throttle it must not shape legitimate inference traffic. A single authenticated client
            // doing RAG/document indexing legitimately issues far more than the MCP surface's 120/min of embedding calls,
            // so the cap is sized for that (100/s) and exists only to bound a runaway/misbehaving local client; real
            // per-model compute is already bounded by the loaded-cap and inference leases. Testing gets the same relaxed
            // treatment so integration runs from one loopback partition stay deterministic.
            options.AddPolicy(NodeAuthRateLimits.LocalModelProxyPolicy, httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(GetRateLimitPartitionKey(httpContext),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = proxyPermitLimit,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            // External integration API. Same shared IP partition function as the three above, deliberately: this
            // middleware runs BEFORE UseAuthentication, so no integration claim exists at partition time and a
            // claim-reading partition function would ship with a branch that can never fire. Per-principal fairness is
            // IntegrationPrincipalRateLimiter, plus the per-principal admission cap inside the accept transaction.
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
                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    message = "Too many auth attempts. Please try again later."
                }, cancellationToken).ConfigureAwait(false);
            };
        });

        builder.Services.AddHostedService<HeartbeatBackgroundService>();
        builder.Services.AddHostedService<AutoConnectBackgroundService>();
        // Chat retention is disabled by default (ChatRetentionOptions.Enabled = false): it permanently deletes user
        // chat history, so it must be explicitly opted into via the ChatRetention config section. Validated on start so
        // a bad window (RetentionDays <= 0 sets the sweep cutoff at/after "now" and would purge everything) fails fast
        // instead of silently deleting all conversations the moment retention is enabled.
        builder.Services.AddOptions<ChatRetentionOptions>()
               .Bind(builder.Configuration.GetSection(ChatRetentionOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();
        builder.Services.AddHostedService<RetentionSweeperService>();
        builder.Services.AddHostedService<SchedulerHistoryRetentionService>();
        // Ages out the append-only agent_execution_logs telemetry (adaptive-memory diagnostics) so it cannot grow
        // unbounded; reads its policy from AgentExecutionLogRetentionOptions (bound in AddNodeAdaptiveMemory).
        builder.Services.AddHostedService<AgentExecutionLogRetentionService>();
        // Startup self-heal: re-stamps every enabled definition's durable Quartz JobDetail with the current dispatch-job
        // type name, so jobs persisted by an older build (whose stored JOB_CLASS_NAME no longer resolves after the
        // dispatch job moved namespaces) load again. Never changes schedules or fires jobs. Registered AFTER
        // AddNodeScheduler so the scheduler factory/job store are available when its StartAsync runs.
        builder.Services.AddHostedService<SchedulerJobDetailReconciliationService>();
        // Seeds the enabled, on-demand (Manual) model-recommendation-check schedule so the React "Refresh now" button
        // works out of the box. Registered AFTER AddNodeScheduler so the scheduler factory/job store are available when
        // the seeder's StartAsync runs (it calls IScheduledJobManagementService, which AddNodeScheduler registers).
        builder.Services.AddHostedService<ModelRecommendationScheduleSeeder>();
        // Seeds the node-local "Default Assistant" agent definition (mode-off persona) so every send resolves through a
        // real, uniformly-selectable definition. Idempotent by slug and self-healing across boots.
        builder.Services.AddHostedService<DefaultAgentSeeder>();
        // Seeds the node-local "Coder (read-only)" agent definition (read/list/search project access) so the read-only
        // coder profile is selectable out of the box. Idempotent by slug and self-healing across boots, like the
        // Default Assistant seeder above.
        builder.Services.AddHostedService<CoderAgentSeeder>();
        // Seeds the node-local "Mathematician" agent definition — the one persona that opts into the sandboxed
        // run_python compute tool, which is profile-opt-in only and therefore unreachable without a definition naming
        // it. Idempotent by slug and self-healing across boots, like the two seeders above. Registered
        // unconditionally, but the seeder itself skips when Compute:Enabled is false (its only tool is refused on a
        // disabled node), so the gate reads the validated ComputeOptions rather than the raw configuration here.
        builder.Services.AddHostedService<MathematicianAgentSeeder>();
        builder.Services.AddHostedService<ToolCallCleanupService>();
        // Encrypts any legacy plaintext message rows (content + metadata_json written before content encryption
        // shipped) into the read-both at-rest envelope. Batched, transactional, resumable, and idempotent — a
        // re-run over an already-encrypted table is a no-op. Registered before the title backfill so titles are
        // re-derived from rows that are already migrated when possible (both are read-both, so order is not required).
        builder.Services.AddHostedService<NodeChatContentEncryptionBackfillService>();
        // One-time L2-normalization of legacy (pre-normalization) chunk vectors so the managed cosine search can score
        // with a dot product. Batched, transactional, resumable, idempotent (re-normalizing a unit vector is a no-op) and
        // safe on an empty database; marker-tracked in chat_maintenance_state. Registered in the Client host only (not the
        // shared KB module) so it never races a test host's fixtures — the search stays correct on the cosine path until
        // it completes, then this flips the singleton IKnowledgeVectorNormalizationState latch to the dot-product path.
        builder.Services.AddHostedService<KnowledgeVectorNormalizationBackfillService>();
        // Re-derives and re-encrypts conversation titles that were NULLed by the EncryptConversationTitle migration
        // (migrations cannot access the node key; this service runs once per startup and is idempotent).
        builder.Services.AddHostedService<NodeChatTitleEncryptionBackfillService>();
        // FRR-2 upgrade backfill: maps any Ollama model pulled on an EARLIER build (which never wrote a provider-map row)
        // to the ollama provider so the flipped llamacpp default does not silently re-route it. Idempotent + offline-
        // tolerant; not desktop-gated (a pre-existing Ollama install can exist on any launch mode).
        builder.Services.AddHostedService<OllamaProviderMapBackfillService>();
        // Desktop-only first-run model provisioning: ensures a small node-local GGUF chat model is installed (via the
        // bundled llama.cpp runtime) and selected so a fresh double-click install can chat out of the box. Gated behind
        // desktop launch mode and offline-tolerant — headless/Aspire/CI never auto-download (off-flag invariant).
        builder.Services.AddHostedService<FirstRunModelProvisioningService>();
        // Opt-in local-model residency keeper. It polls live node settings and periodically touches the selected model so
        // the provider reuses its resident process and refreshes idle age without blocking startup.
        builder.Services.AddHostedService<KeepModelWarmBackgroundService>();
        // One-shot llama.cpp runtime update check: after a short non-blocking delay, resolves the recommended tag against
        // the live release catalog and compares it to the installed runtime, recording an "update available" snapshot
        // (read by the runtime-status endpoint). Notify-only + offline-tolerant; never downloads a binary on its own.
        builder.Services.AddHostedService<LlamaCppUpdateCheckService>();
        // Readiness = essential node-local persistence AND Central Platform worker coordination. Both are tagged "ready"
        // so a failure of either alone flips /health/ready: a dead/unwritable SQLite store must fail readiness even when
        // worker pairing is fine, and vice versa.
        builder.Services.AddHealthChecks()
               .AddCheck<WorkerHealthCheck>("worker_health", tags: ["ready"])
               .AddCheck<NodeSqliteHealthCheck>("node_sqlite", tags: ["ready"]);
    }

    public static void ConfigureJsonSerializerOptions(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

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
