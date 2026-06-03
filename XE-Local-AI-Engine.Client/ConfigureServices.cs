namespace XE_Local_AI_Engine.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSwag;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Represents configure services.
/// </summary>
public static class ConfigureServices
{
    private const string ConsoleOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    public static void AddServices(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        // Serilog
        _ = builder.Services.AddSerilog((serviceCollection, lc) => lc
                                                                   .ReadFrom.Configuration(configuration)
                                                                   .ReadFrom.Services(serviceCollection)
                                                                   .Enrich.FromLogContext()
                                                                   .WriteTo.Console(theme: ConsoleTheme.None, outputTemplate: ConsoleOutputTemplate));

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
        builder.Services.AddHostedService<RetentionSweeperService>();
        builder.Services.AddHostedService<SchedulerHistoryRetentionService>();
        // Seeds the enabled, on-demand (Manual) model-recommendation-check schedule so the React "Refresh now" button
        // works out of the box. Registered AFTER AddNodeScheduler so the scheduler factory/job store are available when
        // the seeder's StartAsync runs (it calls IScheduledJobManagementService, which AddNodeScheduler registers).
        builder.Services.AddHostedService<Services.ModelFit.ModelRecommendationScheduleSeeder>();
        // Seeds the node-local "Default Assistant" agent definition (mode-off persona) so every send resolves through a
        // real, uniformly-selectable definition. Idempotent by slug and self-healing across boots.
        builder.Services.AddHostedService<Services.Agents.Implementation.DefaultAgentSeeder>();
        builder.Services.AddHostedService<ToolCallCleanupService>();
        builder.Services.AddHealthChecks()
               .AddCheck<WorkerHealthCheck>("worker_health", tags: ["ready"])
               .AddCheck<OllamaHealthCheck>("ollama_health", HealthStatus.Unhealthy, ["ready"]);
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
