using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.Agents.AI.DevUI;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting.Server;
using Scalar.AspNetCore;
using Serilog;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Shutdown;

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Desktop mode (self-contained double-click launch) is strictly opt-in via env XE_LAUNCH_MODE=desktop or --desktop.
    // Resolved once, early, so it can gate the loopback bind below and the HTTPS pipeline further down. Off-flag, every
    // call below is skipped and the pipeline is byte-identical to a headless/Aspire/CI run.
    var isDesktop = DesktopLaunch.IsDesktopMode(args);
    if (isDesktop)
    {
        // Bind HTTP on a free loopback port (port 0 = OS picks); the real port is read post-bind for the browser launch.
        builder.WebHost.UseUrls(DesktopLaunch.LoopbackBindUrl);

        // Desktop double-click launch supplies neither the node SQLite connection string nor the operator secret via
        // env/Aspire, so fill them from a per-user data directory here — BEFORE AddServices reads configuration below.
        // Each key is layered in only when absent, so any value already supplied wins and the off-flag (headless/Aspire/
        // CI) path is byte-identical: this branch is never entered without the desktop flag.
        DesktopBootstrap.EnsureLocalDataConfiguration(builder.Configuration);
    }

    builder.Logging.ClearProviders();

    builder.Host.UseDefaultServiceProvider((context, options) =>
    {
        var isDevelopment = context.HostingEnvironment.IsDevelopment();
        options.ValidateScopes = isDevelopment;
        options.ValidateOnBuild = isDevelopment;
    });

    Log.Logger = builder.Environment.CreateStartupLogger();

    // Aspire services
    builder.AddServiceDefaults();

    // Add services to the container.
    builder.AddServices(builder.Configuration);

    // Agent Framework DevUI (development only): a representative named agent plus the
    // OpenAI-compatible Responses/Conversations services the DevUI dashboard requires.
    if (builder.Environment.IsDevelopment())
    {
        builder.AddLocalAiAgentDevUi();
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();
        builder.AddDevUI();
    }

    var app = builder.Build();

    await ApplyNodeChatMigrationsAsync(app.Services).ConfigureAwait(false);
    await ApplyNodeIdentityMigrationsAsync(app.Services).ConfigureAwait(false);
    await RecoverInterruptedNodeChatMessagesAsync(app.Services).ConfigureAwait(false);
    await ReconcileStaleScheduledRunsAsync(app.Services).ConfigureAwait(false);
    ActivateInvocationResumeRegistry(app.Services);
    RegisterWorkerShutdownDrain(app);

    app.UseSerilogRequestLogging(static options =>
    {
        options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
        {
            var redactedQuery = AccessTokenQueryRedactor.Redact(httpContext.Request.QueryString.Value);
            diagnosticContext.Set("RequestPathWithRedactedQuery", $"{httpContext.Request.Path}{redactedQuery}");
            diagnosticContext.Set("QueryString", redactedQuery);
        };
    });

    // Configure the HTTP request pipeline.
    // Standardized typed exception handling (mirrors the central platform): translates domain
    // exceptions into RFC7807 ProblemDetails. Registered before UseFastEndpoints so it wraps endpoints.
    app.UseExceptionHandler();

    // Desktop mode serves plain HTTP on loopback only, so the HTTPS-redirect/HSTS pipeline is
    // bypassed entirely. Off-flag both branches are exactly as before. UseAntiforgery is scheme-agnostic and stays.
    if (!isDesktop)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseHttpsRedirection();
    }

    app.UseAntiforgery();

    app.UseStaticFiles();
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false // don't run any checks; just return 200 if the app can serve requests
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("ready"),
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    duration = e.Value.Duration.TotalMilliseconds
                })
            };
            await context.Response.WriteAsJsonAsync(payload);
        }
    });

    app.UseMiddleware<LocalApiSecurityMiddleware>();
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.UseFastEndpoints(static config =>
    {
        config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix;

        // Single source of truth for OpenAPI operationIds (consumed by the generated hey-api React SDK):
        // derive a clean, camelCase name from the endpoint class name, e.g. CreateScheduledJobEndpoint ->
        // "createScheduledJob". Applied globally (not per-endpoint Description(WithName)) so that FastEndpoints'
        // type-safe Send.CreatedAtAsync<TEndpoint>() Location resolution keeps working — it resolves the target
        // name through this same generator. Class names are unique across the assembly, so operationIds are unique.
        config.Endpoints.NameGenerator = static ctx =>
        {
            var name = ctx.EndpointType.Name;
            if (name.EndsWith("Endpoint", StringComparison.Ordinal) && name.Length > "Endpoint".Length)
            {
                name = name[..^"Endpoint".Length];
            }

            return char.ToLowerInvariant(name[0]) + name[1..];
        };

        config.Errors.UseProblemDetails();
        ConfigureServices.ConfigureJsonSerializerOptions(config.Serializer.Options);
    });
    app.MapHub<LocalChatHub>(LocalApiRoutes.LocalChat.Hub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<SchedulerHub>(LocalApiRoutes.Scheduler.Hub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<PreviewWorkflowHub>(LocalApiRoutes.Preview.Hub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);

    if (!app.Environment.IsProduction())
    {
        app.UseSwaggerGen(static options =>
        {
            options.Path = "/openapi/local/v1/{documentName}.json";
        });

        app.MapScalarApiReference("/scalar", static settings =>
        {
            settings.OpenApiRoutePattern = "/openapi/local/{documentName}/{documentName}.json";

            settings.AddDocument("v1");

            settings.AddPreferredSecuritySchemes("Bearer");
        }).AllowAnonymous();
    }

    // Agent Framework DevUI dashboard (development only) at /devui. The OpenAI-compatible
    // Responses + Conversations endpoints must be mapped before MapDevUI.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
    }

    app.MapFallbackToFile("index.html");

    // Desktop mode only: install console-close → graceful-stop triggers and the on-started browser launch. Off-flag this
    // is never reached, so no signal handler / P/Invoke is installed. The lifecycle is rooted for the
    // app's lifetime via the lifetime token registration; it disposes when the host stops.
    if (isDesktop)
    {
        ActivateDesktopLifecycle(app);
    }

    await app.RunAsync();
}
catch (HostAbortedException)
{
    Log.Information("The Application was aborted");
}
catch (Exception ex)
{
    Log.Fatal(ex, "The Application failed to start");
    throw;
}
finally
{
    Log.Information("Application Stopping");
    await Log.CloseAndFlushAsync();
}

static async Task ApplyNodeChatMigrationsAsync(IServiceProvider services)
{
    ArgumentNullException.ThrowIfNull(services);

    await using var scope = services.CreateAsyncScope();
    var migrationService = scope.ServiceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

    await migrationService.MigrateAsync().ConfigureAwait(false);
}

static async Task ApplyNodeIdentityMigrationsAsync(IServiceProvider services)
{
    ArgumentNullException.ThrowIfNull(services);

    await using var scope = services.CreateAsyncScope();
    var initializationService = scope.ServiceProvider.GetRequiredService<NodeIdentityInitializationService>();

    await initializationService.MigrateAndSeedAsync().ConfigureAwait(false);
}

static async Task RecoverInterruptedNodeChatMessagesAsync(IServiceProvider services)
{
    ArgumentNullException.ThrowIfNull(services);

    await using var scope = services.CreateAsyncScope();
    var recoveryService = scope.ServiceProvider.GetRequiredService<NodeChatRestartRecoveryService>();
    var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

    await recoveryService.RecoverInterruptedMessagesAsync(timeProvider.GetUtcNow().ToUnixTimeMilliseconds()).ConfigureAwait(false);
}

static async Task ReconcileStaleScheduledRunsAsync(IServiceProvider services)
{
    ArgumentNullException.ThrowIfNull(services);

    // A previous process may have died mid-run, leaving Queued/Running rows whose in-memory cancellation registry is
    // gone. Reconcile them to a sanitized terminal state BEFORE the Quartz hosted service starts firing recovery work,
    // so the history never shows a run stuck Running forever. Cheap no-op when there is no scheduler history.
    await using var scope = services.CreateAsyncScope();
    var runStore = scope.ServiceProvider.GetRequiredService<IScheduledJobRunStore>();

    var reconciledCount = await runStore.MarkStaleActiveRunsAsync(ScheduledRunStatus.Failed,
        "Run was interrupted by a node restart and reconciled at startup.").ConfigureAwait(false);

    if (reconciledCount > 0)
    {
        Log.Information("Reconciled {ReconciledCount} stale scheduled job run(s) at startup.", reconciledCount);
    }
}

static void ActivateDesktopLifecycle(WebApplication app)
{
    ArgumentNullException.ThrowIfNull(app);

    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    var server = app.Services.GetRequiredService<IServer>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<DesktopLifecycle>();

    // Ownership is transferred to the host lifetime: the instance lives for the app's lifetime (rooting the native
    // console-ctrl delegate held inside it) and is disposed when the host stops. CA2000 can't see the deferred disposal
    // through the lifetime registration, so it is suppressed with that justification.
#pragma warning disable CA2000 // Disposal is deferred to and owned by ApplicationStopped below.
    var desktopLifecycle = new DesktopLifecycle(lifetime, server, logger);
#pragma warning restore CA2000
    desktopLifecycle.Activate();
    lifetime.ApplicationStopped.Register(desktopLifecycle.Dispose);
}

static void ActivateInvocationResumeRegistry(IServiceProvider services)
{
    ArgumentNullException.ThrowIfNull(services);

    // Eagerly resolve the registry so it subscribes to the dispatcher before any invocation can start,
    // ensuring it observes every live invocation from the first one for reconnect/resume support.
    _ = services.GetRequiredService<IInvocationResumeRegistry>();
}

static void RegisterWorkerShutdownDrain(WebApplication app)
{
    ArgumentNullException.ThrowIfNull(app);

    app.Lifetime.ApplicationStopping.Register(static state =>
    {
        var services = (IServiceProvider)state!;

        try
        {
            var drainService = services.GetRequiredService<IWorkerShutdownDrainService>();
            var result = drainService.DrainAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (!result.Succeeded)
            {
                Log.Warning("Worker shutdown drain completed with incomplete steps. Diagnostics: {Diagnostics}.", result.Diagnostics);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Worker shutdown drain failed before completion. Exception type: {ExceptionType}.",
                exception.GetType().Name);
        }
    }, app.Services);
}

namespace XE_Local_AI_Engine.Client
{
    /// <summary>
    ///     Application entry point for this executable.
    /// </summary>
    public class Program
    {
        protected Program()
        {
        }
    }
}
