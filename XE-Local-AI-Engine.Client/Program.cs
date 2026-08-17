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
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Client.Services.Shutdown;

// IMPORTANT: Velopack hook dispatch must be the first executable statement and remain outside the top-level catch.
// The Windows distribution uses the adjacent C# launcher as its managed executable locator. Linux and development
// hosts retain the default locator. Hook-driven exits must not be logged as startup failures.
FrameworkDependentVelopackBootstrap.Run(args);

// Tracks whether Serilog's real sink (console + rolling file) has been installed yet (see
// Program.StartupLoggerReady). Everything above and the desktop bootstrap inside CreateAppAsync runs while Log.Logger
// is still the silent default, so an exception there would be caught but never written to disk — the "flashes then
// closes, empty logs folder" report. The top-level catch uses the flag to fall back to StartupCrashLog for that
// pre-logger window.
try
{
    var start = await XE_Local_AI_Engine.Client.Program.CreateAppAsync(args).ConfigureAwait(false);
    if (start.App is null)
    {
        return start.ExitCode;
    }

    await start.App.RunAsync().ConfigureAwait(false);
}
catch (HostAbortedException)
{
    Log.Information("The Application was aborted");
}
catch (Exception ex)
{
    // Before the startup logger is installed, Log.Fatal writes nothing (silent default logger), so a crash in the
    // Velopack / desktop bootstrap window would vanish with no console and no log file. Capture it directly to the
    // per-user logs directory so the failure is always diagnosable; once the logger is ready this is redundant with the
    // rolling file and is skipped.
    if (!XE_Local_AI_Engine.Client.Program.StartupLoggerReady)
    {
        StartupCrashLog.Record("The application failed during early startup, before logging was initialized", ex);
    }

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

namespace XE_Local_AI_Engine.Client
{
    using Microsoft.Extensions.Diagnostics.HealthChecks;

    /// <summary>
    ///     Application entry point for this executable.
    /// </summary>
    /// <summary>
    ///     Application entry point for this executable. Also exposes <see cref="CreateAppAsync" />, the directly
    ///     callable app factory the test fixtures build on (no entry-point resolution, no
    ///     HostFactoryResolver.HostingListener thread — see docs/agent-knowledge.md §1).
    /// </summary>
    public sealed class Program
    {
        private Program()
        {
        }

        /// <summary>
        ///     True once the real Serilog sink (console + rolling file) is installed; the top-level catch falls back to
        ///     <see cref="StartupCrashLog" /> while it is still false.
        /// </summary>
        internal static bool StartupLoggerReady { get; private set; }

        /// <summary>
        ///     Builds the fully configured (but unstarted) application: services, migrations, pipeline, endpoint and
        ///     hub mapping — everything the entry point does short of running the server. The entry point calls it with
        ///     no customization; test fixtures pass one to layer test configuration/services and swap in TestServer.
        ///     Returns a null <see cref="ProgramStartResult.App" /> plus exit code for the CLI early-exit paths
        ///     (second desktop instance, knowledge-downgrade commands, admin password reset).
        /// </summary>
        public static async Task<ProgramStartResult> CreateAppAsync(string[] args, ProgramAppCustomization? customization = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            // Held for the process lifetime once acquired in the desktop branch below; disposed after the host is built.
            SingleInstanceLease? instanceLease = null;

            // Desktop mode (packaged double-click launch) is enabled by env XE_LAUNCH_MODE=desktop / --desktop, AND is
            // implied by a Velopack-managed install (installer or portable): that packaged flavor IS the desktop app — its in-app
            // updater is desktop-only — but the Velopack stub launches the managed entry point without the env/arg a manual
            // launcher sets, so the install itself is the opt-in signal. FrameworkDependentVelopackBootstrap.Run(args) above
            // established the locator this reads. Resolved once, early — BEFORE the builder — so it can pin the content root just
            // below, gate the loopback bind, and gate the HTTPS pipeline further down. A raw DLL/dev/Aspire/CI run is not a
            // Velopack install and sets no env/arg, so every desktop-gated call is skipped and the pipeline is byte-identical.
            // A customized (test-fixture) app is never the packaged desktop app, and VelopackLocator throws unless the
            // entry point's FrameworkDependentVelopackBootstrap.Run established it — so don't consult it on the test path.
            var isDesktop = customization is null && DesktopLaunch.IsDesktopMode(args, VelopackInstall.IsManaged());

            // In desktop mode the app is launched from an arbitrary working directory (a double-click, or the documented
            // `cd ~/Applications && ./XE-Local-AI-Engine.AppImage`), so pin the content root to the executable's own directory —
            // where the shipped appsettings.json and wwwroot live — instead of the default current directory. Windows was masked
            // by its C# launcher forcing WorkingDirectory to the base dir; the Linux AppImage has no launcher, so without this the
            // shipped appsettings.json is never found and startup fails on WorkerNode:NodeName. Off the flag we leave the default
            // current-directory content root so headless/Aspire/CI/dev behavior is byte-identical.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = customization?.ContentRootPath ?? (isDesktop ? AppContext.BaseDirectory : null),
                EnvironmentName = customization?.EnvironmentName,
                WebRootPath = customization?.WebRootPath,
            });

            // App self-update build flavor: layer the baked, channel-specific update config (repo URL +
            // stable/RC release track) over the appsettings defaults. The publish output renames the active
            // appsettings.AppUpdate.{flavor}.json to appsettings.AppUpdate.json (see the Client .csproj), so exactly one
            // channel file is present and it can never point a tester build at the main repo. Optional: absent in dev/CI, where
            // the empty appsettings.json defaults leave the updater inert.
            builder.Configuration.AddJsonFile("appsettings.AppUpdate.json", optional: true, reloadOnChange: false);

            if (customization?.Configuration is { Count: > 0 } configurationOverrides)
            {
                builder.Configuration.AddInMemoryCollection(configurationOverrides);
            }

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
                    StartupLoggerReady = true;
                    Log.Fatal("Another instance of XE Local AI Engine is already running for the data directory '{DataDirectory}'. "
                              + "Close the other instance before starting a new one.", desktopDataDirectory);
                    await Log.CloseAndFlushAsync().ConfigureAwait(false);
                    return new ProgramStartResult(App: null, ExitCode: 1);
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
            StartupLoggerReady = true;

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

            // App self-update (Velopack + anonymous public GitHub releases). Desktop-mode only: off the flag this registers nothing and the
            // desktop-only endpoints are filtered out of FastEndpoints above. The process args are re-passed on relaunch so the
            // new version comes back up in desktop mode and re-binds the persisted loopback port.
            builder.AddAppUpdate(builder.Configuration, isDesktop, args);

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

            // Applied last so a test can override registrations and swap the server (e.g. TestServer).
            customization?.ConfigureBuilder?.Invoke(builder);

            var app = builder.Build();

            // Transfer the single-instance lease to the host lifetime: it lives until shutdown and releases the exclusive lock
            // on ApplicationStopped. The OS also releases it on a crash, so this is graceful cleanup rather than a correctness
            // requirement. Null off the desktop flag (no lease is acquired there).
            if (instanceLease is not null)
            {
                app.Lifetime.ApplicationStopped.Register(instanceLease.Dispose);
            }

            // The downgrade commands must inspect/export the schema exactly as it is on disk. Run them before the ordinary
            // startup migration path so invoking a newer binary never changes the database before reporting compatibility.
            var knowledgeDowngradeCommand = DesktopLaunch.GetKnowledgeDowngradeCommand(args);
            if (knowledgeDowngradeCommand != KnowledgeDowngradeCommand.None)
            {
                var downgradeExitCode = await RunKnowledgeDowngradeCommandAsync(app.Services, knowledgeDowngradeCommand).ConfigureAwait(false);
                instanceLease?.Dispose();
                return new ProgramStartResult(App: null, downgradeExitCode);
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
                return new ProgramStartResult(App: null, resetExitCode);
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
            // Skipped in the Testing environment, where the permit limits are relaxed to non-limits anyway (see
            // ConfigureServices): RateLimitingMiddleware never disposes its PartitionedRateLimiter (verified against
            // Microsoft.AspNetCore.RateLimiting 10.0), so its 100ms replenishment RunTimer outlives host disposal and
            // GC-roots the middleware pipeline — logger → DI root scope → the ENTIRE host — for the process lifetime.
            // That immortal-timer root is one of the reasons every disposed test host leaked ~20 MB (gcroot evidence in
            // docs/agent-knowledge.md §1). RequireRateLimiting endpoint metadata stays registered and is inert without
            // the middleware; no test asserts 429s.
            if (!app.Environment.IsEnvironment("Testing"))
            {
                app.UseRateLimiter();
            }
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseFastEndpoints(config =>
            {
                config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix;

                // Desktop-only app self-update surface: off the desktop flag these endpoints are excluded from
                // registration entirely, so the routes are absent (a request 404s) rather than throwing a 500 for a missing
                // service. Mirrors the invariant that the updater is desktop-mode only. On the desktop flag the filter is a
                // no-op (returns true for every endpoint).
                // Note this filter cannot gate an endpoint whose SERVICES are conditionally registered: FastEndpoints
                // instantiates every discovered endpoint before evaluating it (which is why the AppUpdate services are
                // registered in every mode). Development Mode's endpoints are excluded at DISCOVERY instead — see the
                // EndpointDiscoveryOptions.Filter in ConfigureServices.
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
            app.MapHub<BenchmarkRunHub>(LocalApiRoutes.Benchmarks.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);
            app.MapHub<DatasetGenerationHub>(LocalApiRoutes.Training.DatasetGenerationHub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);
            app.MapHub<TrainingRuntimeHub>(LocalApiRoutes.Training.RuntimeHub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);
            app.MapHub<TrainingRunHub>(LocalApiRoutes.Training.RunHub)
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

            // The inbound OpenAI-compatible model proxy. Hand-mapped here (like MapMcp) rather than as FastEndpoints, for the
            // same load-bearing reasons: (1) the paths sit INSIDE /api/local/v1 so LocalApiSecurityMiddleware's loopback +
            // Host + Origin gate already covers them — moving them out of the prefix would silently drop that gate and leave the
            // bearer key as the only control; (2) the request/response is arbitrary OpenAI JSON + SSE, not a node DTO, so it
            // must NOT appear in the OpenAPI document or the generated React SDK — only external tools talk to it. The
            // LocalModelProxy policy accepts ONLY the model-proxy API key scheme, never the operator's JWT or the MCP key.
            var proxyRoutePrefix = $"/{LocalApiRoutes.Prefix}/";
            app.MapGet(proxyRoutePrefix + LocalApiRoutes.Proxy.Models,
                   static (HttpContext context, LocalModelProxyForwarder forwarder) => forwarder.WriteModelsAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.LocalModelProxy)
               .RequireRateLimiting(NodeAuthRateLimits.LocalModelProxyPolicy);
            app.MapPost(proxyRoutePrefix + LocalApiRoutes.Proxy.ChatCompletions,
                   static (HttpContext context, LocalModelProxyForwarder forwarder) => forwarder.ForwardChatCompletionsAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.LocalModelProxy)
               .RequireRateLimiting(NodeAuthRateLimits.LocalModelProxyPolicy);
            app.MapPost(proxyRoutePrefix + LocalApiRoutes.Proxy.Embeddings,
                   static (HttpContext context, LocalModelProxyForwarder forwarder) => forwarder.ForwardEmbeddingsAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.LocalModelProxy)
               .RequireRateLimiting(NodeAuthRateLimits.LocalModelProxyPolicy);

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

            app.MapFallbackToFile("index.html");

            // Desktop mode only: install console-close → graceful-stop triggers and the on-started browser launch. Off-flag this
            // is never reached, so no signal handler / P/Invoke is installed. The lifecycle is rooted for the
            // app's lifetime via the lifetime token registration; it disposes when the host stops.
            if (isDesktop)
            {
                ActivateDesktopLifecycle(app);
            }


            return new ProgramStartResult(app, ExitCode: 0);
        }

        // Request-completion level for UseSerilogRequestLogging: failures stay loud (5xx/exception = Error, unexpected 4xx =
        // Warning) while routine traffic (2xx/3xx, the 401 token-refresh dance, SPA-fallback 404s) drops to Debug so the SPA's
        // polling does not dominate the rolling log file.
        private static LogEventLevel GetRequestCompletionLogLevel(HttpContext httpContext, Exception? exception)
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

        private static async Task ApplyNodeChatMigrationsAsync(IServiceProvider services)
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

        private static async Task ApplyNodeIdentityMigrationsAsync(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            await using var scope = services.CreateAsyncScope();
            var initializationService = scope.ServiceProvider.GetRequiredService<NodeIdentityInitializationService>();

            await initializationService.MigrateAndSeedAsync().ConfigureAwait(false);
        }

        private static async Task<int> ResetAdminPasswordAsync(IServiceProvider services, string? newPassword)
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

            Log.Information("Admin password reset succeeded. Refresh tokens revoked and existing access tokens invalidated; "
                            + "sign in with the new password.");
            return 0;
        }

        private static async Task<int> RunKnowledgeDowngradeCommandAsync(IServiceProvider services, KnowledgeDowngradeCommand command)
        {
            ArgumentNullException.ThrowIfNull(services);

            try
            {
                var safetyService = services.GetRequiredService<IKnowledgeDowngradeSafetyService>();
                KnowledgeDowngradePreflightResult preflight;

                if (command == KnowledgeDowngradeCommand.Export)
                {
                    var export = await safetyService.ExportAsync(CancellationToken.None).ConfigureAwait(false);
                    preflight = export.Preflight;
                    Log.Information("Knowledge downgrade backup exported to {ArtifactPath} ({ArtifactBytes} bytes, SHA-256 {ArtifactSha256}).",
                        export.ArtifactPath,
                        export.ArtifactBytes,
                        export.ArtifactSha256);
                }
                else
                {
                    preflight = await safetyService.PreflightAsync(CancellationToken.None).ConfigureAwait(false);
                }

                Log.Information("Knowledge downgrade preflight: migrationApplied={MigrationApplied}, compatible={Compatible}, "
                                + "conflictGroups={ConflictGroups}, conflictingDocuments={ConflictingDocuments}, minimumRemovals={MinimumRemovals}.",
                    preflight.CollectionMigrationApplied,
                    preflight.IsCompatible,
                    preflight.ConflictGroupCount,
                    preflight.ConflictingDocumentCount,
                    preflight.MinimumDocumentsToRemove);

                foreach (var conflict in preflight.Conflicts)
                {
                    Log.Warning("Knowledge downgrade {ConflictId}: opaque document identifiers {DocumentIdentifiers}.",
                        conflict.ConflictId,
                        conflict.DocumentIdentifiers);
                }

                if (!preflight.IsCompatible)
                {
                    Log.Error("Knowledge downgrade is blocked. No data was modified; resolve conflicts explicitly or restore the exported backup.");
                    return 3;
                }

                return 0;
            }
            catch (Exception exception)
            {
                Log.Error(exception, "Knowledge downgrade preflight/export failed. No downgrade was attempted.");
                return 1;
            }
        }

        private static async Task RecoverInterruptedNodeChatMessagesAsync(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            await using var scope = services.CreateAsyncScope();
            var recoveryService = scope.ServiceProvider.GetRequiredService<NodeChatRestartRecoveryService>();
            var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            await recoveryService.RecoverInterruptedMessagesAsync(timeProvider.GetUtcNow().ToUnixTimeMilliseconds()).ConfigureAwait(false);
        }

        private static async Task ReconcileStaleScheduledRunsAsync(IServiceProvider services)
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

        private static void ActivateDesktopLifecycle(WebApplication app)
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

        private static void ActivateInvocationResumeRegistry(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);

            // Eagerly resolve the registry so it subscribes to the dispatcher before any invocation can start,
            // ensuring it observes every live invocation from the first one for reconnect/resume support.
            _ = services.GetRequiredService<IInvocationResumeRegistry>();
        }

        private static void RegisterWorkerShutdownDrain(WebApplication app)
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
    }

    /// <summary>
    ///     Test seam for <see cref="Program.CreateAppAsync" />: environment/web-root overrides must be applied at
    ///     WebApplicationOptions time, configuration before AddServices reads it, and <see cref="ConfigureBuilder" />
    ///     runs after every product registration (override services, call UseTestServer) just before Build().
    /// </summary>
    public sealed class ProgramAppCustomization
    {
        public string? EnvironmentName { get; init; }

        public string? ContentRootPath { get; init; }

        public string? WebRootPath { get; init; }

        public IReadOnlyDictionary<string, string?>? Configuration { get; init; }

        public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }
    }

    /// <summary>
    ///     Result of <see cref="Program.CreateAppAsync" />: the built app, or a null app plus the process exit code
    ///     when a CLI early-exit path handled the invocation.
    /// </summary>
    public sealed record ProgramStartResult(WebApplication? App, int ExitCode);
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
            return context.Response.WriteAsJsonAsync(BuildPayload(report), CancellationToken.None);
        }
    }
}
