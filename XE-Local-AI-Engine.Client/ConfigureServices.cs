namespace XE_Local_AI_Engine.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;
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
using XE_Local_AI_Engine.Client.Endpoints.Common;
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
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
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
        });

        // Explicit, stable Data Protection key-ring. The framework already auto-registers Data Protection (so the
        // encrypted token stores — CloudCredentialStore, CodexTokenStore, HfTokenStore, GitHubTokenStore, the auth
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
        if (OperatingSystem.IsWindows())
        {
            dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: false);
        }
        else
        {
            dataProtection.Services.AddSingleton<INodeDataProtectionKeyProvider, NodeDataProtectionKeyProvider>();
            dataProtection.Services.AddSingleton<NodeDataProtectionKeyRingEncryptor>();
            dataProtection.Services.AddOptions<KeyManagementOptions>()
                          .Configure<NodeDataProtectionKeyRingEncryptor>((options, encryptor) => options.XmlEncryptor = encryptor);

            // Hard-fail instead of silently regenerating the ring when an ENCRYPTED key cannot be decrypted (BE-02
            // follow-up). Data Protection's DefaultKeyResolver swallows a per-key decrypt failure and, finding no usable
            // default, generates a fresh key — orphaning every stored credential/token. Decorate that resolver so a
            // wrong/missing operator secret surfaces as a loud startup failure. The decoration is skipped if the
            // framework ever stops registering the resolver by implementation type (then the pre-existing behavior
            // stands), so it can never break a correct install.
            var defaultKeyResolver = dataProtection.Services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDefaultKeyResolver));
            if (defaultKeyResolver?.ImplementationType is { } innerResolverType)
            {
                dataProtection.Services.Remove(defaultKeyResolver);
                dataProtection.Services.AddSingleton<IDefaultKeyResolver>(serviceProvider =>
                    new NodeDataProtectionKeyRingFailClosedKeyResolver((IDefaultKeyResolver)ActivatorUtilities.CreateInstance(serviceProvider, innerResolverType)));
            }
        }

        // Application layer (services, options, persistence, runtime) lives in the
        // XE-Local-AI-Engine.Client.Application class library. The host only wires web-framework
        // concerns below (FastEndpoints, auth, SignalR, rate limiting, health checks, hosted services).
        builder.AddNodeApplication(configuration);

        // Quartz scheduler runtime (persistent store + hosted service + dispatcher). Registers nothing when
        // Scheduler:Enabled is false. The QRTZ_ tables are created by the same node-chat EF migration.
        builder.AddNodeScheduler(configuration);

        // Hub-backed scheduler event publisher — supersedes the no-op default registered in AddNodeScheduler so
        // run/definition lifecycle events broadcast to connected SignalR clients (SchedulerHub mapped in Program).
        builder.Services.AddSingleton<ISchedulerEventPublisher, SchedulerEventPublisher>();

        // Hub-backed preview-workflow event publisher — supersedes the no-op default registered in AddNodePreviewWorkflows
        // so run/node lifecycle events broadcast to the connected operator (PreviewWorkflowHub mapped in Program).
        builder.Services.AddSingleton<IPreviewWorkflowEventPublisher, PreviewWorkflowEventPublisher>();

        // Hub-backed GGUF download event publisher — supersedes the no-op default registered in AddNodeModelFit so
        // download status changes push live to operator clients (GgufDownloadHub mapped in Program), replacing the
        // per-second downloads poll. IHubContext is singleton-safe, so the singleton coordinator can resolve it.
        builder.Services.AddSingleton<IGgufDownloadEventPublisher, GgufDownloadEventPublisher>();

        // Hub-backed in-app CUDA build event publisher — supersedes the no-op default the provider registers so build
        // phase + log lines push live to operator clients (CudaBuildHub mapped in Program). IHubContext is singleton-safe.
        builder.Services.AddSingleton<ICudaBuildEventPublisher, CudaBuildEventPublisher>();
        builder.Services.AddSingleton<ILlamaCppSourceBuildEventPublisher, LlamaCppSourceBuildEventPublisher>();

        // Hub-backed knowledge-base indexing notifier — supersedes the no-op default registered in AddNodeKnowledgeBase so
        // document status changes push live to operator clients (KnowledgeBaseHub mapped in Program). IHubContext is
        // singleton-safe, so the scoped ingestion service can resolve this singleton.
        builder.Services.AddSingleton<IKnowledgeIndexingNotifier, KnowledgeIndexingNotifier>();

        // Hub-backed image-job event publisher — supersedes the no-op default registered in AddNodeImages so coarse job
        // status transitions push live to operator clients (ImageJobHub mapped in Program). IHubContext is singleton-safe,
        // so the singleton image-job coordinator can resolve it.
        builder.Services.AddSingleton<IImageJobEventPublisher, ImageJobEventPublisher>();
        builder.Services.AddSingleton<IStableDiffusionCppSourceBuildEventPublisher, StableDiffusionCppSourceBuildEventPublisher>();

        // Development ships enabled. Keep the no-op publisher only when the administrator explicitly disables it.
        if (configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true))
        {
            builder.Services.AddSingleton<IDevelopmentAttemptLiveEventPublisher, DevelopmentAttemptLiveEventPublisher>();
        }

        // Error handling - the order of the exception handlers is important: specific handlers first,
        // DefaultExceptionHandler last as the catch-all 500. Mirrors the central platform's IExceptionHandler pattern.
        builder.Services
               .AddExceptionHandler<ConflictExceptionHandler>()
               .AddExceptionHandler<DefaultExceptionHandler>();
        builder.Services.AddProblemDetails();

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

                // NJsonSchema emits CLR member names for string enums; honor [JsonStringEnumMemberName]
                // so the OpenAPI enum values match the wire format (e.g. host-agent runtime-status enums
                // serialize "running"/"managed", not "Running"/"Managed"). Without this, generated client
                // validators reject valid responses.
                settings.SchemaSettings.SchemaProcessors.Add(new JsonStringEnumMemberNameSchemaProcessor());
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
