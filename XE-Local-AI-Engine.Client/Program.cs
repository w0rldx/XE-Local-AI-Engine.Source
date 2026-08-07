using System.Diagnostics;
using FastEndpoints;
using FastEndpoints.Swagger;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Persistence;
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

    // Operator hint (BE-03): purely informational, fires once at startup, never gates or alters telemetry
    // registration. AddServiceDefaults/ConfigureOpenTelemetry above always instruments gen_ai spans/metrics; only the
    // OTLP exporter is conditional on OTEL_EXPORTER_OTLP_ENDPOINT (AddOpenTelemetryExporters,
    // XE-Local-AI-Engine.ServiceDefaults/Extensions.cs). Aspire auto-injects that variable, so this stays silent
    // there; desktop/RC and other headless launches leave it unset by default, so telemetry stays in-process only and
    // is lost on exit unless the operator sets it. See docs/runbooks/otel-export-operator-runbook.md.
    if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
        Log.Information("Telemetry export is OFF (OTEL_EXPORTER_OTLP_ENDPOINT is not set): gen_ai spans/metrics are "
                        + "recorded in-process only and are lost on exit. Set OTEL_EXPORTER_OTLP_ENDPOINT to export "
                        + "them to a local OTLP collector; see docs/runbooks/otel-export-operator-runbook.md.");
    }

    // Add services to the container.
    var isDevelopmentModeEnabled = builder.Configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true);
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
    // Activity id format and registering a listener for the ASP.NET Core hosting source makes ASP.NET create a request
    // Activity from an inbound `traceparent` header even when no OTel listener is present; otherwise Activity.Current
    // would be null in the request pipeline and the emitted trace id would regress to the Kestrel connection id
    // (TraceIdentifier). The listener is scoped to "Microsoft.AspNetCore" — the only source that produces the request
    // activities this correlation needs — rather than every source in the process. AllData makes the listener request
    // all data for activities that scoped source creates, so their W3C trace/span ids are populated; it does not by
    // itself record them (that would need AllDataAndRecorded). The emitted traceresponse trace-flags byte follows the
    // activity's actual recorded state: with only this listener attached (no started TracerProvider) activities are
    // never recorded, so the byte is "00" regardless of the inbound sampled flag. In the normal host the OpenTelemetry
    // TracerProvider is also running (AddServiceDefaults/ConfigureOpenTelemetry registers it in every mode), and its
    // default ParentBased(AlwaysOn) sampler DOES record — a sampled inbound parent then yields "01", an unsampled
    // parent stays "00". Process-global, so set once before Build().
    Activity.DefaultIdFormat = ActivityIdFormat.W3C;
    Activity.ForceDefaultIdFormat = true;
#pragma warning disable CA2000 // The listener is owned by the static ActivitySource registry for the app's lifetime.
    ActivitySource.AddActivityListener(new ActivityListener
    {
        ShouldListenTo = static source => source.Name == "Microsoft.AspNetCore",
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

    // Local admin password recovery (operator-run, single-user machine). Handled here — after identity migrations
    // guarantee the tables + Admin role exist, and BEFORE the web host serves — so the reset runs against the SAME
    // database the app uses (the desktop branch above already filled the connection string + operator key) and the
    // single-instance lease already proved no other instance is holding that data directory. Resets the admin password
    // without the old one, revokes all refresh tokens, then exits; off the flag this is a no-op and startup continues.
    if (DesktopLaunch.TryGetResetAdminPassword(args, out var resetPassword))
    {
        var resetExitCode = await ResetAdminPasswordAsync(app.Services, resetPassword).ConfigureAwait(false);
        instanceLease?.Dispose();
        return resetExitCode;
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
                // The trace-flags byte reflects the activity's actual recorded state rather than a hardcoded "01"
                // (see TraceResponseHeader.Build), so a downstream reader is not told the span was sampled when it was not.
                if (!context.Response.Headers.ContainsKey(TraceResponseHeader.HeaderName))
                {
                    context.Response.Headers[TraceResponseHeader.HeaderName] = TraceResponseHeader.Build(activity);
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
        // Make the status→HTTP mapping explicit and intentional. Healthy and Degraded both return 200 —
        // a Degraded worker (e.g. an expired platform-pairing token) is still serving local inference, so readiness
        // consumers (Aspire's WithHttpHealthCheck poll) must keep it in rotation — but the payload now distinguishes it
        // with per-check status + description + reason data, so "degraded" is never a silent 200. Only Unhealthy (a dead
        // node-SQLite store) fails readiness with 503.
        ResultStatusCodes = new Dictionary<HealthStatus, int>
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = ReadinessHealthResponse.WriteAsync
    });

    if (!isDevelopmentModeEnabled)
    {
        var developmentPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Development.Root}");
        var capabilityPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Development.Capability}");
        // Keep the disabled capability opaque before local API security or authentication can challenge the caller.
        // The endpoint types remain discoverable process-wide because FastEndpoints caches endpoint discovery across
        // WebApplicationFactory instances; service and hub registration still remain disabled with this feature flag.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(developmentPath, StringComparison.OrdinalIgnoreCase)
                && !context.Request.Path.Equals(capabilityPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

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
    app.MapHub<LlamaCppSourceBuildHub>(LocalApiRoutes.ModelFit.SourceBuildHub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<RuntimeAcquisitionHub>(LocalApiRoutes.ModelFit.LlamaCppAcquisitionHub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<KnowledgeBaseHub>(LocalApiRoutes.KnowledgeBase.Hub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<ImageJobHub>(LocalApiRoutes.Images.Hub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    app.MapHub<StableDiffusionCppSourceBuildHub>(LocalApiRoutes.Images.RuntimeSourceBuildHub)
       .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    if (isDevelopmentModeEnabled)
    {
        app.MapHub<DevelopmentAttemptHub>(LocalApiRoutes.Development.Hub)
           .RequireAuthorization(NodeAuthorizationPolicies.Operator);
    }

    // The inbound MCP Streamable HTTP endpoint. Mapped here, beside the hubs, for two reasons that are both
    // load-bearing:
    //   1. The path sits INSIDE /api/local/v1, so LocalApiSecurityMiddleware (registered well above, before
    //      UseRouting) has already enforced loopback peer + allowed Host + same-origin Origin on it. That middleware
    //      matches on the /api/local/v1 prefix ALONE — mapping this at a bare "/mcp" would silently drop the entire
    //      loopback gate and leave the bearer key as the only control. Do not move it out of the prefix.
    //   2. It is mapped outside UseFastEndpoints (like MapHub) because MapMcp owns its own JSON-RPC transport rather
    //      than being a FastEndpoints endpoint; it therefore does not appear in the OpenAPI document, and the React
    //      client never talks to it — only external MCP clients do.
    // The McpServer policy accepts ONLY the MCP API key scheme, never the operator's JWT.
    app.MapMcp($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerEndpoint}")
       .RequireAuthorization(NodeAuthorizationPolicies.McpServer)
       .RequireRateLimiting(NodeAuthRateLimits.McpPolicy);

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

// Honor any non-zero exit code set during startup/shutdown (e.g. the loopback-bind guard's guarded shutdown), rather
// than always reporting success on a graceful stop.
return Environment.ExitCode;

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

    // BE-06: snapshot the node database before applying pending migrations, in the same scope. Best-effort — a backup
    // failure is logged and swallowed inside the service, so it can never block migration or brick startup.
    var backupService = scope.ServiceProvider.GetRequiredService<INodeDbBackupService>();
    await backupService.BackupBeforeMigrationAsync().ConfigureAwait(false);

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

static async Task<int> ResetAdminPasswordAsync(IServiceProvider services, string? newPassword)
{
    ArgumentNullException.ThrowIfNull(services);

    if (string.IsNullOrWhiteSpace(newPassword))
    {
        // ponytail: password passed on argv — acceptable on a local single-operator machine (the trust boundary is the
        // machine), and it avoids a console-subsystem stdin prompt that the packaged GUI exe cannot reliably show.
        Log.Error("The {Flag} flag requires a new password argument, e.g. the flag followed by <NEW_PASSWORD>.", DesktopLaunch.ResetAdminPasswordArgument);
        return 2;
    }

    await using var scope = services.CreateAsyncScope();
    var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();

    var result = await authService.ResetAdminPasswordAsync(newPassword, CancellationToken.None).ConfigureAwait(false);
    if (!result.Succeeded)
    {
        Log.Error("Admin password reset failed: {Errors}", string.Join(" ", result.Errors));
        return 1;
    }

    Log.Information("Admin password reset succeeded. All existing sessions were signed out; sign in with the new password.");
    return 0;
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
    using Microsoft.Extensions.Diagnostics.HealthChecks;

    /// <summary>
    ///     Application entry point for this executable.
    /// </summary>
    public class Program
    {
        protected Program()
        {
        }
    }

    /// <summary>
    ///     Projects a readiness <see cref="HealthReport" /> into the <c>/health/ready</c> JSON payload. Each
    ///     check reports its own status, description, and structured reason data, so a Degraded worker that still returns
    ///     HTTP 200 (it is serving local inference) is nonetheless distinguishable by an inspecting operator or dashboard.
    /// </summary>
    public static class ReadinessHealthResponse
    {
        public static object BuildPayload(HealthReport report)
        {
            ArgumentNullException.ThrowIfNull(report);

            return new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(static entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    reason = entry.Value.Data.Count == 0
                        ? null
                        : entry.Value.Data.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                    duration = entry.Value.Duration.TotalMilliseconds
                }).ToArray()
            };
        }

        public static Task WriteAsync(HttpContext context, HealthReport report)
        {
            ArgumentNullException.ThrowIfNull(context);

            context.Response.ContentType = "application/json";
            return context.Response.WriteAsJsonAsync(BuildPayload(report));
        }
    }
}
