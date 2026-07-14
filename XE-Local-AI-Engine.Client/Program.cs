using System.Diagnostics;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Velopack;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Shutdown;
#if DEBUG
using Microsoft.Agents.AI.DevUI;
#endif

// Velopack install, update, and uninstall hook dispatch. This MUST be the FIRST executable statement and is
// intentionally placed BEFORE the try/catch. When Velopack is invoked for an install, update, or uninstall hook it runs
// the hook and exits the process, and that hook-driven exit must NOT be intercepted by the app's top-level catch, which
// logs a fatal startup failure and rethrows. On a normal launch the call returns immediately, so every host — desktop,
// headless, Aspire, and CI — proceeds unchanged. It is safe in all modes.
VelopackApp.Build().Run();

try
{
    // Held for the process lifetime once acquired in the desktop branch below; disposed after the host is built.
    SingleInstanceLease? instanceLease = null;

    var builder = WebApplication.CreateBuilder(args);

    // App self-update build flavor: layer the baked, channel-specific update config (repo URL +
    // GitHub App client_id) over the appsettings defaults. The publish output renames the active
    // appsettings.AppUpdate.{flavor}.json to appsettings.AppUpdate.json (see the Client .csproj), so exactly one
    // channel file is present and it can never point a tester build at the main repo. Optional: absent in dev/CI, where
    // the empty appsettings.json defaults leave the updater inert.
    builder.Configuration.AddJsonFile("appsettings.AppUpdate.json", optional: true, reloadOnChange: false);

    // Desktop mode (self-contained double-click launch) is enabled by env XE_LAUNCH_MODE=desktop / --desktop, AND is
    // implied by a Velopack-managed install (installer or portable): that packaged flavor IS the desktop app — its in-app
    // updater is desktop-only — but the Velopack stub launches the bare exe without the env/arg a manual launcher sets,
    // so the install itself is the opt-in signal. VelopackApp.Build().Run() above established the locator this reads.
    // Resolved once, early, so it can gate the loopback bind below and the HTTPS pipeline further down. A raw-exe/dev/
    // Aspire/CI run is not a Velopack install and sets no env/arg, so every call below is skipped and the pipeline is
    // byte-identical to before.
    var isDesktop = DesktopLaunch.IsDesktopMode(args, VelopackInstall.IsManaged());
    if (isDesktop)
    {
        // Resolve (and create) the per-user data dir up front so both the bind below and the config layer share it.
        var desktopDataDirectory = DesktopBootstrap.ResolveDataDirectory();

        // Acquire the exclusive per-data-root lease BEFORE any key/DB initialization (the operator key is generated in
        // EnsureLocalDataConfiguration just below). A second concurrent instance sharing this data directory would each
        // generate its own encryption key and split the DB, so fail fast here with a clear message and a non-zero exit
        // rather than proceeding. Held for the process lifetime; disposed on shutdown after the host is built. Off the
        // desktop flag this is never reached — headless/Aspire/CI do not share this per-user data root.
#pragma warning disable CA2000 // Ownership is transferred to the host lifetime (disposed via ApplicationStopped below).
        instanceLease = SingleInstanceLease.TryAcquire(desktopDataDirectory);
#pragma warning restore CA2000
        if (instanceLease is null)
        {
            Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);
            Log.Fatal("Another instance of XE Local AI Engine is already running for the data directory '{DataDirectory}'. "
                      + "Close the other instance before starting a new one.", desktopDataDirectory);
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
            return 1;
        }

        // Re-bind the loopback port remembered from the last launch when it is still free, so the browser origin
        // (scheme+host+port) stays stable and localStorage-backed user prefs survive between runs; otherwise fall back to
        // a fresh OS-assigned port (:0). The actually-bound port is read post-bind and persisted for next time.
        builder.WebHost.UseUrls(DesktopPortStore.ResolveBindUrl(desktopDataDirectory));

        // Desktop double-click launch supplies neither the node SQLite connection string nor the operator secret via
        // env/Aspire, so fill them from the per-user data directory here — BEFORE AddServices reads configuration below.
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

    Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);

    // Aspire services
    builder.AddServiceDefaults();

    // Add services to the container.
    builder.AddServices(builder.Configuration);

    // App self-update (Velopack + GitHub device flow). Desktop-mode only: off the flag this registers nothing and the
    // desktop-only endpoints are filtered out of FastEndpoints above. The process args are re-passed on relaunch so the
    // new version comes back up in desktop mode and re-binds the persisted loopback port.
    builder.AddAppUpdate(builder.Configuration, isDesktop, args);

    // Agent Framework DevUI (development only): a representative named agent plus the
    // OpenAI-compatible Responses/Conversations services the DevUI dashboard requires.
    // Compiled out of Release entirely so the preview/alpha DevUI + Hosting packages
    // never ship in the published desktop build; the IsDevelopment() gate stays as
    // defense in depth for Debug builds run against a non-Development environment.
#if DEBUG
    if (builder.Environment.IsDevelopment())
    {
        builder.AddLocalAiAgentDevUi();
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();
        builder.AddDevUI();
    }
#endif

    // W3C trace correlation that works with Aspire/OpenTelemetry OFF (the desktop/RC default). Forcing the W3C
    // Activity id format and registering a no-op ActivityListener makes ASP.NET create a request Activity from an
    // inbound `traceparent` header even when no OTel listener is present; otherwise Activity.Current would be null in
    // the request pipeline and the emitted trace id would regress to the Kestrel connection id (TraceIdentifier).
    // When Aspire is ON, the OTel listener coexists with this one. Process-global, so set once before Build().
    Activity.DefaultIdFormat = ActivityIdFormat.W3C;
    Activity.ForceDefaultIdFormat = true;
#pragma warning disable CA2000 // The no-op listener is owned by the static ActivitySource registry for the app's lifetime.
    ActivitySource.AddActivityListener(new ActivityListener
    {
        ShouldListenTo = static _ => true,
        Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
    });
#pragma warning restore CA2000

    var app = builder.Build();

    // Transfer the single-instance lease to the host lifetime: it lives until shutdown and releases the exclusive lock
    // on ApplicationStopped. The OS also releases it on a crash, so this is graceful cleanup rather than a correctness
    // requirement. Null off the desktop flag (no lease is acquired there).
    if (instanceLease is not null)
    {
        app.Lifetime.ApplicationStopped.Register(instanceLease.Dispose);
    }

    // Loopback-only bind guard (defense-in-depth behind LocalApiSecurityMiddleware): shut down if the server bound a
    // routable address without the Security:AllowNonLoopbackBind opt-out. A no-op on every supported launch (desktop
    // binds 127.0.0.1; Aspire binds localhost and exposes externally via the DCP proxy).
    LoopbackBindGuard.Guard(app);

    try
    {
        await ApplyNodeChatMigrationsAsync(app.Services).ConfigureAwait(false);
        await ApplyNodeIdentityMigrationsAsync(app.Services).ConfigureAwait(false);
        Log.Information("Database migrations applied.");
    }
    catch (Exception migrationException)
    {
        // Fail-loud is unchanged (the migration services already run transactionally and rethrow); this only adds a
        // targeted error line with the cause before the top-level catch logs the generic fatal + rethrows.
        Log.Error(migrationException, "Database migrations failed to apply.");
        throw;
    }

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

        // Keep failures loud but drop routine traffic below the default Information floor: the SPA polls several
        // endpoints (auth status, download/job progress, health), so at Information the request log dominates the
        // rolling file (~60% of all lines measured) and buries the diagnostic entries the file sink exists for.
        // 401 (routine token-refresh dance) and 404 (SPA fallback probing) stay at Debug with the successes.
        options.GetLevel = static (httpContext, _, ex) => GetRequestCompletionLogLevel(httpContext, ex);
    });

    // Configure the HTTP request pipeline.
    // Standardized typed exception handling (mirrors the central platform): translates domain
    // exceptions into RFC7807 ProblemDetails. Registered before UseFastEndpoints so it wraps endpoints.
    app.UseExceptionHandler();

    // Emit the W3C trace id on the success path too, so the local diagnostics snapshot can correlate a 2xx response
    // with backend logs (the error path carries the same id via ProblemDetails.traceId). The header is set in
    // Response.OnStarting so it lands before the body flushes, and an already-present value is never overwritten.
    app.Use(static async (context, next) =>
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            context.Response.OnStarting(() =>
            {
                if (!context.Response.Headers.ContainsKey("traceresponse"))
                {
                    context.Response.Headers["traceresponse"] = $"00-{activity.TraceId}-{activity.SpanId}-01";
                }

                return Task.CompletedTask;
            });
        }

        await next().ConfigureAwait(false);
    });

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

    app.UseFastEndpoints(config =>
    {
        config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix;

        // Desktop-only surface (app self-update + GitHub auth): off the desktop flag these endpoints are excluded from
        // registration entirely, so the routes are absent (a request 404s) rather than throwing a 500 for a missing
        // service. Mirrors the invariant that the updater is desktop-mode only. On the desktop flag the filter is a
        // no-op (returns true for every endpoint).
        config.Endpoints.Filter = ep => isDesktop || !typeof(IDesktopOnlyEndpoint).IsAssignableFrom(ep.EndpointType);

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
    app.MapHub<GgufDownloadHub>(LocalApiRoutes.ModelFit.DownloadHub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<CudaBuildHub>(LocalApiRoutes.ModelFit.CudaBuildHub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<KnowledgeBaseHub>(LocalApiRoutes.KnowledgeBase.Hub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<ImageJobHub>(LocalApiRoutes.Images.Hub)
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
    // Responses + Conversations endpoints must be mapped before MapDevUI. Compiled out of
    // Release (see the registration block above).
#if DEBUG
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
    }
#endif

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

return 0;

// Request-completion level for UseSerilogRequestLogging: failures stay loud (5xx/exception = Error, unexpected 4xx =
// Warning) while routine traffic (2xx/3xx, the 401 token-refresh dance, SPA-fallback 404s) drops to Debug so the SPA's
// polling does not dominate the rolling log file.
static LogEventLevel GetRequestCompletionLogLevel(HttpContext httpContext, Exception? exception)
{
    if (exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
    {
        return LogEventLevel.Error;
    }

    return httpContext.Response.StatusCode is >= StatusCodes.Status400BadRequest
        and not StatusCodes.Status401Unauthorized
        and not StatusCodes.Status404NotFound
        ? LogEventLevel.Warning
        : LogEventLevel.Debug;
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

    // The per-user data dir the desktop branch set (see Program top); the lifecycle persists the bound loopback port
    // there post-start so the next launch can re-bind it for a stable browser origin.
    var desktopDataDirectory = app.Configuration[DesktopBootstrap.NodeDataDirectoryKey];

    // Ownership is transferred to the host lifetime: the instance lives for the app's lifetime (rooting the native
    // console-ctrl delegate held inside it) and is disposed when the host stops. CA2000 can't see the deferred disposal
    // through the lifetime registration, so it is suppressed with that justification.
#pragma warning disable CA2000 // Disposal is deferred to and owned by ApplicationStopped below.
    var desktopLifecycle = new DesktopLifecycle(lifetime, server, logger, desktopDataDirectory);
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
            var drainOptions = services.GetRequiredService<IOptions<WorkerShutdownDrainOptions>>().Value;

            // The drain enforces its own end-to-end deadline internally. This is a hard outer ceiling so that a stage
            // which fails to honor that token (a non-cancellable await) still cannot block process shutdown forever:
            // wait at most the configured deadline plus a grace, then abandon the remaining steps.
            var configuredTimeout = drainOptions.DrainTimeout > TimeSpan.Zero
                ? drainOptions.DrainTimeout
                : WorkerShutdownDrainOptions.DefaultDrainTimeout;
            var hardCeiling = configuredTimeout + TimeSpan.FromSeconds(5);

            var drainTask = drainService.DrainAsync(CancellationToken.None);
            if (!drainTask.Wait(hardCeiling))
            {
                Log.Warning("Worker shutdown drain exceeded its hard ceiling of {HardCeilingSeconds}s; abandoning remaining steps.",
                    hardCeiling.TotalSeconds);
                return;
            }

            var result = drainTask.GetAwaiter().GetResult();
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
