using Serilog;
using XE_Local_AI_Engine.Client.Hosting;

// IMPORTANT: Velopack hook dispatch must be the first executable statement and stay outside the top-level catch, because hook-driven exits must not be
// logged as startup failures. The Windows distribution uses the adjacent C# launcher as its managed executable locator; Linux and dev keep the default.
FrameworkDependentVelopackBootstrap.Run(args);

// Everything above, and the desktop bootstrap inside CreateAppAsync, runs while Log.Logger is still the silent default, so an exception there would be caught
// but never written to disk — the "flashes then closes, empty logs folder" report. Program.StartupLoggerReady is what routes that window to StartupCrashLog.
try
{
    var start = await XE_Local_AI_Engine.Client.Program.CreateAppAsync(args);
    if (start.App is null)
    {
        return start.ExitCode;
    }

    await start.App.RunAsync();
}
catch (HostAbortedException)
{
    Log.Information("The Application was aborted");
}
catch (Exception ex)
{
    // Before the startup logger is installed Log.Fatal writes nothing, so a crash in the Velopack / desktop bootstrap window would vanish with no console and
    // no log file. Capture it directly to the per-user logs directory; once the logger is ready this is redundant with the rolling file and is skipped.
    if (!XE_Local_AI_Engine.Client.Program.StartupLoggerReady)
    {
        await StartupCrashLog.RecordAsync("The application failed during early startup, before logging was initialized", ex, CancellationToken.None);
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
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using FastEndpoints;
    using FastEndpoints.Swagger;
    using Microsoft.AspNetCore.Diagnostics.HealthChecks;
    using Microsoft.Extensions.Diagnostics.HealthChecks;
    using Microsoft.Net.Http.Headers;
    using Scalar.AspNetCore;
    using Serilog;
    using XE_Local_AI_Engine.Client.Common.Extensions;
    using XE_Local_AI_Engine.Client.Endpoints.Common;
    using XE_Local_AI_Engine.Client.Hosting;
    using XE_Local_AI_Engine.Client.Hubs;
    using XE_Local_AI_Engine.Client.Services.Auth;
    using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
    using XE_Local_AI_Engine.Client.Services.Development;
    using XE_Local_AI_Engine.Client.Services.DevWorkflows;
    using XE_Local_AI_Engine.Client.Services.ExternalApps;
    using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
    using XE_Local_AI_Engine.Client.Services.Integrations;
    using XE_Local_AI_Engine.Client.Services.Proxy;
    using XE_Local_AI_Engine.Client.Services.Transcription;
    using XE_Local_AI_Engine.Client.Services.WorkSessions;

    /// <summary>
    ///     Application entry point for this executable. Also exposes <see cref="CreateAppAsync" />, the directly
    ///     callable app factory the test fixtures build on (no entry-point resolution, no
    ///     HostFactoryResolver.HostingListener thread — see docs/agent-knowledge.md §1).
    /// </summary>
    public sealed partial class Program
    {
        [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
            Justification = "The entry-point container must not expose a constructible public API.")]
        private Program()
        {
        }

        /// <summary>
        ///     Marks the SPA shell fallback endpoint so the middleware just after <c>UseRouting</c> can recognise it
        ///     and detach it from requests under the local API prefix. A marker rather than a second routed fallback:
        ///     see the comment at that middleware.
        /// </summary>
        private sealed class SpaFallbackMarker;

        /// <summary>
        ///     True once the real Serilog sink (console + rolling file) is installed; the top-level catch falls back to
        ///     <see cref="StartupCrashLog" /> while it is still false.
        /// </summary>
        internal static bool StartupLoggerReady { get; private set; }

        /// <summary>
        ///     Builds the fully configured (but unstarted) application: services, migrations, pipeline, endpoint and
        ///     hub mapping — everything the entry point does short of running the server.
        /// </summary>
        /// <remarks>
        ///     The entry point calls it with no customization; test fixtures pass one to layer test configuration and
        ///     services and to swap in TestServer. Returns a null <see cref="ProgramStartResult.App" /> plus an exit
        ///     code for the CLI early-exit paths: a second desktop instance, the knowledge-downgrade commands, and the
        ///     admin password reset.
        /// </remarks>
        public static async Task<ProgramStartResult> CreateAppAsync(string[] args, ProgramAppCustomization? customization = null)
        {
            ArgumentNullException.ThrowIfNull(args);
            var parentPipeValue = DesktopParentLifetime.TakeEnvironmentValue();
            if (!DesktopLaunch.HasOneShotCommand(args))
            {
                var launchMode = customization is null
                    ? DesktopLaunch.ResolveLaunchMode(args, VelopackInstall.IsManaged())
                    : LaunchMode.Headless;
                var pipeName = DesktopParentLifetime.ResolvePipeName(launchMode, parentPipeValue);
                DesktopParentLifetime? parentLifetime = null;
                try
                {
                    if (pipeName is not null)
                    {
#pragma warning disable CA2000 // Failure/null-result disposal is in finally; successful bootstrap transfers ownership to the resolved DI singleton.
                        parentLifetime = new DesktopParentLifetime(pipeName, TimeProvider.System, Environment.Exit);
#pragma warning restore CA2000
                        await parentLifetime.StartAsync(CancellationToken.None);
                    }

                    var result = await CreateAppCoreAsync(args, customization, commandContext: null, parentLifetime);
                    if (result.App is not null)
                    {
                        parentLifetime = null;
                    }

                    return result;
                }
                finally
                {
                    if (parentLifetime is not null)
                    {
                        await parentLifetime.DisposeAsync();
                    }
                }
            }

            var standardError = customization?.StandardError ?? Console.Error;
            var commandContext = new OneShotCommandContext();
            try
            {
                customization?.BeforeOneShotCommand?.Invoke();
                return await CreateAppCoreAsync(args, customization, commandContext);
            }
            catch (Exception exception)
            {
                await standardError.WriteLineAsync($"The engine command failed unexpectedly (stage={commandContext.StageOutput}, type={exception.GetType().Name}).");
                if (exception is DesktopDataDirectoryException dataDirectoryException)
                {
                    await standardError.WriteLineAsync(dataDirectoryException.SafeDiagnostic);
                }

                return new ProgramStartResult { App = null, ExitCode = 1 };
            }
        }

        private static async Task<ProgramStartResult> CreateAppCoreAsync(string[] args,
            ProgramAppCustomization? customization,
            OneShotCommandContext? commandContext,
            DesktopParentLifetime? parentLifetime = null)
        {
            var standardOutput = customization?.StandardOutput ?? Console.Out;
            var standardError = customization?.StandardError ?? Console.Error;

            if (DesktopLaunch.HasHelpFlag(args))
            {
                await WriteHelpAsync(standardOutput);
                return new ProgramStartResult { App = null, ExitCode = 0 };
            }

            // Status is always a one-shot command, even when a serve flag is also present. It is intentionally handled
            // before builder creation so inspecting a running instance never acquires its lease or mutates its data dir.
            if (DesktopLaunch.HasStatusFlag(args))
            {
                commandContext?.SetStage(OneShotCommandStage.Status);
                var isManagedInstall = customization is null && VelopackInstall.IsManaged();
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = await StatusCommandAsync(args,
                        isManagedInstall,
                        standardOutput,
                        standardError,
                        customization?.StatusHttpClientFactory)
                };
            }

            if (!DesktopLaunch.TryGetPort(args, out var requestedPort, out var portError))
            {
                await standardError.WriteLineAsync(portError);
                return new ProgramStartResult { App = null, ExitCode = 2 };
            }

            var setupRequested = DesktopLaunch.TryGetSetupCommand(args, out var setupCommand, out var setupError);
            if (setupRequested && setupError is not null)
            {
                await standardError.WriteLineAsync(setupError);
                return new ProgramStartResult { App = null, ExitCode = 2 };
            }

            if (setupCommand?.PasswordFromEnvironment == true)
            {
                Environment.SetEnvironmentVariable(DesktopLaunch.AdminPasswordEnvironmentVariable, null);
            }

            var mcpKeyRequested = DesktopLaunch.TryGetMcpKeyScope(args, out var mcpKeyScope, out var mcpKeyError);
            if (mcpKeyRequested && mcpKeyError is not null)
            {
                await standardError.WriteLineAsync(mcpKeyError);
                return new ProgramStartResult { App = null, ExitCode = 3 };
            }

            commandContext?.SetStage(OneShotCommandStage.HostInitialization);

            // Held for the process lifetime once acquired in the desktop branch below; disposed after the host is built.
            SingleInstanceLease? instanceLease = null;

            // Desktop mode comes from XE_LAUNCH_MODE=desktop / --desktop and is implied by a Velopack-managed install, whose stub sets neither, so the install is the opt-in signal.
            // Resolved once BEFORE the builder, so it can pin the content root, the loopback bind and the HTTPS pipeline. Never consulted on the test path: VelopackLocator THROWS there.
            var launchMode = customization is null
                ? DesktopLaunch.ResolveLaunchMode(args, VelopackInstall.IsManaged())
                : LaunchMode.Headless;
            var isLocalMode = launchMode.IsLocalMode();
            var needsLocalData = isLocalMode || setupRequested || mcpKeyRequested;
            var stableRestartArgs = DesktopLaunch.BuildRestartArguments(args, launchMode, requestedPort, shellOwned: parentLifetime is not null);
            var hostArgs = args;
            if (setupRequested || mcpKeyRequested)
            {
                hostArgs = DesktopLaunch.HasExplicitLocalModeArgument(args) ? [.. stableRestartArgs] : [];
            }

            // A desktop launch starts from an arbitrary working directory, so pin the content root to the executable's own directory, where the shipped appsettings.json and
            // wwwroot live. Windows was masked by its launcher forcing WorkingDirectory; the Linux AppImage has none, so without this startup fails on WorkerNode:NodeName.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = hostArgs,
                ContentRootPath = customization?.ContentRootPath ?? (isLocalMode ? AppContext.BaseDirectory : null),
                EnvironmentName = customization?.EnvironmentName,
                WebRootPath = customization?.WebRootPath,
            });

            // Layer the baked, channel-specific update config (repo URL + stable/RC track) over the appsettings defaults. The publish output renames the active
            // appsettings.AppUpdate.{flavor}.json, so exactly one channel file is present and a tester build can never point at the main repo. Absent in dev/CI.
            builder.Configuration.AddJsonFile("appsettings.AppUpdate.json", optional: true, reloadOnChange: false);

            if (customization?.Configuration is { Count: > 0 } configurationOverrides)
            {
                builder.Configuration.AddInMemoryCollection(configurationOverrides);
            }

            if (needsLocalData)
            {
                // Resolve (and create) the per-user data dir up front so both the bind below and the config layer share it.
                var desktopDataDirectory = DesktopBootstrap.ResolveDataDirectory();

                // Acquire the exclusive per-data-root lease BEFORE any key/DB initialization, which happens in EnsureLocalDataConfiguration just below: two concurrent instances
                // sharing this directory would each generate a key and split the DB. Held for the process lifetime and disposed on shutdown; never reached off the desktop flag.
#pragma warning disable CA2000 // Ownership is transferred to the host lifetime (disposed via ApplicationStopped below).
                instanceLease = SingleInstanceLease.TryAcquire(desktopDataDirectory);
#pragma warning restore CA2000
                if (instanceLease is null)
                {
                    Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);
                    StartupLoggerReady = true;
                    Log.Fatal("Another instance of XE Local AI Engine is already running for the data directory '{DataDirectory}'. "
                              + "Close the other instance before starting a new one.", desktopDataDirectory);
                    if (setupRequested || mcpKeyRequested)
                    {
                        Log.Error("The engine is already running for this data directory. Use the HTTP path on the running "
                                  + "instance, or stop it before running this command.");
                    }

                    await Log.CloseAndFlushAsync();
                    return new ProgramStartResult { App = null, ExitCode = setupRequested || mcpKeyRequested ? 4 : 1 };
                }

                // Re-bind the remembered loopback port while it is still free, so the browser origin stays stable and localStorage-backed prefs survive between
                // runs; otherwise take a fresh OS-assigned port. The actually-bound port is read post-bind and persisted for next time.
                if (requestedPort is { } port)
                {
                    if (!DesktopPortStore.IsPortAvailable(port))
                    {
                        Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);
                        StartupLoggerReady = true;
                        Log.Fatal("Port {Port} is already in use; --port does not fall back automatically.", port);
                        await Log.CloseAndFlushAsync();
                        instanceLease.Dispose();
                        return new ProgramStartResult { App = null, ExitCode = 6 };
                    }

#pragma warning disable S5332 // Local mode intentionally binds plain HTTP exclusively on 127.0.0.1; it never leaves the machine.
                    builder.WebHost.UseUrls($"http://{DesktopLaunch.LoopbackHost}:{port}");
#pragma warning restore S5332
                }
                else
                {
                    builder.WebHost.UseUrls(await DesktopPortStore.ResolveBindUrlAsync(desktopDataDirectory, CancellationToken.None));
                }

                // A double-click launch supplies neither the node SQLite connection string nor the operator secret, so fill them from the per-user data directory
                // BEFORE AddServices reads configuration below. Each key is layered in only when absent, so any value already supplied wins.
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

            // Operator hint: informational only, once at startup, never gating telemetry registration — only the OTLP exporter is conditional on this variable. Aspire
            // auto-injects it, so this stays silent there; a headless launch leaves it unset, so telemetry is lost on exit. See docs/runbooks/otel-export-operator-runbook.md.
            if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            {
                Log.Information("Telemetry export is OFF (OTEL_EXPORTER_OTLP_ENDPOINT is not set): gen_ai spans/metrics are "
                                + "recorded in-process only and are lost on exit. Set OTEL_EXPORTER_OTLP_ENDPOINT to export "
                                + "them to a local OTLP collector; see docs/runbooks/otel-export-operator-runbook.md.");
            }

            var isDevelopmentModeEnabled = builder.Configuration.GetValue($"{DevelopmentOptions.Section}:Enabled", defaultValue: true);
            var areWorkSessionsEnabled = builder.Configuration.GetValue($"{WorkSessionOptions.Section}:Enabled", defaultValue: false);
            var areDevWorkflowsEnabled = builder.Configuration.GetValue($"{DevWorkflowOptions.Section}:Enabled", defaultValue: false);
            var areGraphWorkflowsEnabled = builder.Configuration.GetValue($"{GraphWorkflowOptions.Section}:Enabled", defaultValue: true);
            var isTranscriptionEnabled = builder.Configuration.GetValue($"{TranscriptionOptions.Section}:Enabled", defaultValue: true);
            var areExternalAppsEnabled = builder.Configuration.GetValue($"{ExternalAppsOptions.SectionName}:Enabled", defaultValue: false);

            // The container bridge, the engine's ONE deliberately non-loopback listener, so an application container can reach this node's inference surface — a container
            // cannot reach the host's loopback. BOTH flags, because a node with External Apps off has no containers, and each defaults to false, so a missing config opens nothing.
            var bridgeEndpoint = areExternalAppsEnabled
                ? ResolveContainerBridgeEndpoint(builder)
                : null;

            // Published BEFORE AddServices, because the bind address resolves during host construction and the services that tell a container about the bridge are built after
            // this line. Registered even when the bridge did not open: a missing registration would be a startup failure rather than the honest "this node has no bridge".
            builder.Services.AddSingleton(new ContainerBridgeEndpointSource(bridgeEndpoint));
            if (parentLifetime is not null)
            {
                builder.Services.AddSingleton<DesktopParentLifetime>(_ => parentLifetime);
            }

            builder.AddServices(builder.Configuration);

            // App self-update (Velopack + anonymous public GitHub releases), desktop-mode only: off the flag this registers nothing and the desktop-only endpoints are
            // filtered out of FastEndpoints above. The process args are re-passed on relaunch, so the new version comes back up in desktop mode on the persisted port.
            builder.AddAppUpdate(builder.Configuration,
                launchMode,
                stableRestartArgs,
                shellOwned: parentLifetime is not null);

            // W3C trace correlation that works with Aspire/OpenTelemetry OFF, the desktop/RC default: without it Activity.Current is null in the request pipeline and the
            // emitted trace id regresses to the Kestrel connection id. Process-global, set once before Build(). See docs/wiki/11-hosting-and-deployment.md ("The node host pipeline (`Program.cs`)").
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
            if (parentLifetime is not null)
            {
                app.Services.GetRequiredService<DesktopParentLifetime>().Bind(app.Lifetime);
            }

            // Transfer the single-instance lease to the host lifetime: it lives until shutdown and releases the exclusive lock on ApplicationStopped. The OS releases it on a
            // crash too, so this is graceful cleanup rather than a correctness requirement. Null off the desktop flag, where no lease is acquired.
            if (instanceLease is not null)
            {
                app.Lifetime.ApplicationStopped.Register(instanceLease.Dispose);
            }

            // The downgrade commands must inspect/export the schema exactly as it is on disk. Run them before the ordinary
            // startup migration path so invoking a newer binary never changes the database before reporting compatibility.
            var knowledgeDowngradeCommand = DesktopLaunch.GetKnowledgeDowngradeCommand(args);
            if (knowledgeDowngradeCommand != KnowledgeDowngradeCommand.None)
            {
                var downgradeExitCode = await RunKnowledgeDowngradeCommandAsync(app.Services, knowledgeDowngradeCommand);
                instanceLease?.Dispose();
                return new ProgramStartResult { App = null, ExitCode = downgradeExitCode };
            }

            // Loopback-only bind guard, defense-in-depth behind LocalApiSecurityMiddleware: shut down if the server bound a routable address without the
            // Security:AllowNonLoopbackBind opt-out. A no-op on every supported launch — desktop binds 127.0.0.1, Aspire binds localhost behind the DCP proxy.
            LoopbackBindGuard.Guard(app,
                bridgeEndpoint is null
                    ? []
                    : new[]
                    {
                        bridgeEndpoint.ListenerUrl
                    });

            try
            {
                // Split around the migration pass on purpose, and written IMMEDIATELY after the node-chat pass. See
                // docs/wiki/11-hosting-and-deployment.md ("The node host pipeline (`Program.cs`)").
                var pendingCanvasWorkflows = await ReadPendingCanvasWorkflowsAsync(app.Services);

                commandContext?.SetStage(OneShotCommandStage.Migrations);
                await ApplyNodeChatMigrationsAsync(app.Services);
                await ImportCanvasWorkflowsAsync(app.Services, pendingCanvasWorkflows);
                await ApplyNodeIdentityMigrationsAsync(app.Services);
                Log.Information("Database migrations applied.");
            }
            catch (Exception migrationException)
            {
                // Fail-loud is unchanged (the migration services already run transactionally and rethrow); this only adds a
                // targeted error line with the cause before the top-level catch logs the generic fatal + rethrows.
                Log.Error(migrationException, "Database migrations failed to apply.");
                throw;
            }

            // Local admin password recovery, handled AFTER identity migrations guarantee the tables and Admin role and BEFORE the web host serves, so it runs against the SAME
            // database the app uses and the single-instance lease has already proved nothing else holds the directory. Resets without the old password, revokes refresh tokens, exits.
            if (DesktopLaunch.TryGetResetAdminPassword(args, out var resetPassword))
            {
                var resetExitCode = await ResetAdminPasswordAsync(app.Services, resetPassword);
                instanceLease?.Dispose();
                return new ProgramStartResult { App = null, ExitCode = resetExitCode };
            }

            if (setupRequested)
            {
                commandContext?.SetStage(OneShotCommandStage.Handler);
                var setupExitCode = await SetupCommandAsync(app.Services, setupCommand!, standardOutput, standardError);
                if (setupExitCode != 0)
                {
                    instanceLease?.Dispose();
                    return new ProgramStartResult { App = null, ExitCode = setupExitCode };
                }
            }

            if (mcpKeyRequested)
            {
                commandContext?.SetStage(OneShotCommandStage.Handler);
                var mcpKeyExitCode = await McpKeyCommandAsync(app.Services, mcpKeyScope!.Value, standardOutput, standardError);
                if (mcpKeyExitCode != 0)
                {
                    instanceLease?.Dispose();
                    return new ProgramStartResult { App = null, ExitCode = mcpKeyExitCode };
                }
            }

            if ((setupRequested || mcpKeyRequested) && !DesktopLaunch.HasExplicitLocalModeArgument(args))
            {
                instanceLease?.Dispose();
                return new ProgramStartResult { App = null, ExitCode = 0 };
            }

            await RecoverInterruptedNodeChatMessagesAsync(app.Services);
            await ReconcileStaleScheduledRunsAsync(app.Services);
            ActivateInvocationResumeRegistry(app.Services);

            app.UseSerilogRequestLogging(ConfigureRequestLogging);

            // Central exception handling preserves each typed family contract: Training handlers write their custom JSON shapes, while Benchmark and other
            // ProblemDetails handlers use RFC 7807. Registered before UseFastEndpoints, so it wraps endpoints.
            app.UseExceptionHandler();

            // FIRST branch in the pipeline, and first for a load-bearing reason: everything registered below belongs to the loopback listener alone. See
            // docs/wiki/11-hosting-and-deployment.md ("The container bridge listener").
            if (bridgeEndpoint is not null)
            {
                ContainerBridgePipeline.Map(app, bridgeEndpoint);
            }

            // Apply response-wide security/correlation headers at the shared boundary, before static files, health checks, authentication, endpoints and the SPA
            // fallback. OnStarting is what makes even a short-circuit response carry the anti-framing defense. An existing trace header is never overwritten.
            app.Use(static async (context, next) =>
            {
                var activity = Activity.Current;
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[HeaderNames.XFrameOptions] = "DENY";
                    if (activity is not null && !context.Response.Headers.ContainsKey(TraceResponseHeader.HeaderName))
                    {
                        // The trace-flags byte reflects the activity's actual recorded state rather than a hardcoded "01"
                        // (see TraceResponseHeader.Build), so a downstream reader is not told the span was sampled when it was not.
                        context.Response.Headers[TraceResponseHeader.HeaderName] = TraceResponseHeader.Build(activity);
                    }

                    return Task.CompletedTask;
                });

                await next();
            });

            // Desktop mode serves plain HTTP on loopback only, so the HTTPS-redirect/HSTS pipeline is
            // bypassed entirely. Off-flag both branches are exactly as before. UseAntiforgery is scheme-agnostic and stays.
            if (!isLocalMode)
            {
                if (!app.Environment.IsDevelopment())
                {
                    app.UseHsts();
                }

                app.UseHttpsRedirection();
            }

            app.UseAntiforgery();

            app.UseMiddleware<NativeDesktopDocumentPolicy>();
            app.UseStaticFiles();
            // AllowAnonymous is load-bearing, not decorative: under the FallbackPolicy an endpoint with no auth
            // metadata is challenged, and Aspire's WithHttpHealthCheck poll carries no token.
            app.MapHealthChecks("/health/live", new HealthCheckOptions
            {
                Predicate = _ => false // don't run any checks; just return 200 if the app can serve requests
            }).AllowAnonymous();

            app.MapHealthChecks("/health/ready", new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("ready"),
                // Explicit and intentional: Healthy and Degraded both return 200, because a Degraded worker is still serving local inference and readiness consumers such as
                // Aspire's WithHttpHealthCheck poll must keep it in rotation, while the payload distinguishes it per check. Only Unhealthy — a dead node-SQLite store — is 503.
                ResultStatusCodes = new Dictionary<HealthStatus, int>
                {
                    [HealthStatus.Healthy] = StatusCodes.Status200OK,
                    [HealthStatus.Degraded] = StatusCodes.Status200OK,
                    [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
                },
                ResponseWriter = ReadinessHealthResponse.WriteAsync
            }).AllowAnonymous();

            if (!isDevelopmentModeEnabled)
            {
                var developmentPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Development.Root}");
                var capabilityPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Development.Capability}");
                // Endpoint discovery is per host (the EndpointDiscoveryOptions.Filter in ConfigureServices), so these routes are genuinely absent on a disabled node.
                // This middleware still answers FIRST, ahead of local API security and authentication, so the disabled capability cannot be probed by status code.
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(developmentPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.Equals(capabilityPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
            }

            if (!areWorkSessionsEnabled)
            {
                // Unlike Development, the work-session endpoints and hub stay DISCOVERED when the feature is off, because dropping them would drop the whole family out of
                // the OpenAPI document and the generated client; only behaviour is gated, here, ahead of security and authentication so the switch cannot be probed.
                var workSessionPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.WorkSessions.Root}");
                // The one carve-out, like Development's: the capability GET stays reachable so the SPA can say the
                // feature is switched off rather than rendering this bodyless 404 as a load failure.
                var workSessionCapabilityPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.WorkSessions.Capability}");
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(workSessionPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.Equals(workSessionCapabilityPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
            }

            if (!areDevWorkflowsEnabled)
            {
                // Same posture as work sessions, and for the same reason: the endpoints and the hub stay discovered so the OpenAPI document, and the client generated from
                // it, is the same on every node. Behaviour is gated here instead, ahead of local API security and authentication, so the switch cannot be probed.
                var devWorkflowPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.DevelopmentWorkflows.Root}");
                // The one carve-out, like Development's: the capability GET stays reachable so the SPA can say the
                // feature is switched off rather than rendering this bodyless 404 as a load failure.
                var devWorkflowCapabilityPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.DevelopmentWorkflows.Capability}");
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(devWorkflowPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.Equals(devWorkflowCapabilityPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
            }

            if (!areGraphWorkflowsEnabled)
            {
                // The graph-workflow family holds the same posture as the two blocks above: discovered with the feature off, so the document and the generated client are
                // identical on every node, with only behaviour gated here — ahead of security and authentication, so it answers 404 before anything can answer 403.
                var graphWorkflowPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.GraphWorkflows.Root}");
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(graphWorkflowPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
            }

            if (!isTranscriptionEnabled)
            {
                // The same posture as the three blocks above and for the same reasons: the endpoints stay DISCOVERED with the feature off, so the document and the generated
                // client are identical on every node, with only behaviour gated — ahead of security and authentication, so it answers 404 before anything can answer 403.
                var transcriptionPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Transcription.Root}");
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(transcriptionPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
            }

            if (!areExternalAppsEnabled)
            {
                // The external-apps family holds the same posture as the three blocks above, and the prefix check covers the hub's negotiate too, since that path shares the
                // family's first segment. Sitting ahead of local API security and authentication is what makes the switch answer 404 before anything can answer 401 or 403.
                var externalAppsPath = new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.ExternalApps.Root}");
                app.Use(async (context, next) =>
                {
                    if (context.Request.Path.StartsWithSegments(externalAppsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await next(context);
                });
            }

            app.UseMiddleware<LocalApiSecurityMiddleware>();
            app.UseRouting();

            // The SPA shell must not answer for the local API: detaching the selected endpoint here, rather than mapping a second fallback over the prefix, is
            // deliberate and is pinned by ValidateExecutableEndpointTests. See docs/wiki/09-api-and-hubs.md ("Static SPA fallback").
            var localApiPrefixPath = new PathString($"/{LocalApiRoutes.Prefix}");
            app.Use(async (context, next) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<SpaFallbackMarker>() is not null
                    && context.Request.Path.StartsWithSegments(localApiPrefixPath, StringComparison.OrdinalIgnoreCase))
                {
                    context.SetEndpoint(endpoint: null);
                }

                await next(context);
            });
            // Skipped in the Testing environment, where the permit limits are relaxed to non-limits anyway: the middleware's undisposed replenishment timer GC-roots the
            // whole host. See docs/wiki/11-hosting-and-deployment.md ("The node host pipeline (`Program.cs`)") and docs/agent-knowledge.md §1.
            if (!app.Environment.IsEnvironment("Testing"))
            {
                app.UseRateLimiter();
            }

            // Ahead of authentication ON PURPOSE: UseSwaggerGen is raw middleware with no endpoint for .AllowAnonymous() to attach to, and the early position is how the
            // dev-only document is served unauthenticated, not an accident. See docs/wiki/09-api-and-hubs.md ("Dev-only surfaces").
            if (!app.Environment.IsProduction())
            {
                app.UseSwaggerGen(static options =>
                {
                    options.Path = "/openapi/local/v1/{documentName}.json";
                });
            }

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseFastEndpoints(config =>
            {
                config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix;

                // Deny by default: every discovered endpoint gets the Operator policy whether or not its own Configure() asked for it, so a forgotten Policies() call can
                // never ship an anonymous route. See docs/wiki/09-api-and-hubs.md ("Security middleware & auth ordering") for why it is unconditional and what it costs.
                config.Endpoints.Configurator = ep => ep.Policies(NodeAuthorizationPolicies.Operator);

                // Desktop-only app self-update surface: off the flag these routes are absent rather than 500ing on a missing service. This filter cannot gate an endpoint whose
                // SERVICES are conditionally registered, because FastEndpoints instantiates every discovered endpoint before evaluating it — that is EndpointDiscoveryOptions.Filter's job.
                config.Endpoints.Filter = ep => isLocalMode || !typeof(IDesktopOnlyEndpoint).IsAssignableFrom(ep.EndpointType);

                // Single source of truth for OpenAPI operationIds, consumed by the generated hey-api React SDK: a camelCase name derived from the endpoint class name. Applied
                // globally, not per-endpoint, so FastEndpoints' type-safe Send.CreatedAtAsync<TEndpoint>() Location resolution keeps working through this same generator.
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
            app.MapHub<WorkSessionHub>(LocalApiRoutes.WorkSessions.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);
            app.MapHub<TranscriptionHub>(LocalApiRoutes.Transcription.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            // Mapped unconditionally, like the work-session hub: DevWorkflows:Enabled is enforced by the request-path
            // middleware above, which answers 404 for the whole prefix — including this path.
            app.MapHub<DevWorkflowRunHub>(LocalApiRoutes.DevelopmentWorkflows.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            // Same posture for graph workflows: GraphWorkflows:Enabled is enforced by the request-path middleware
            // above, which answers 404 for the whole prefix — this path included.
            app.MapHub<GraphWorkflowRunHub>(LocalApiRoutes.GraphWorkflows.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            // And for external apps: ExternalApps:Enabled is enforced by the request-path middleware above, which
            // answers 404 for the whole prefix — this path and its negotiate included.
            app.MapHub<ExternalAppHub>(LocalApiRoutes.ExternalApps.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            if (isDevelopmentModeEnabled)
            {
                app.MapHub<DevelopmentAttemptHub>(LocalApiRoutes.Development.Hub)
                   .RequireAuthorization(NodeAuthorizationPolicies.Operator);
            }

            // The inbound MCP Streamable HTTP endpoint, mapped beside the hubs. Its path MUST stay inside /api/local/v1 — do not move it — and the McpServer policy accepts
            // ONLY the MCP API key scheme, never the operator's JWT. See docs/wiki/09-api-and-hubs.md ("Notable non-typed routes").
            app.MapMcp($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerEndpoint}")
               .RequireAuthorization(NodeAuthorizationPolicies.McpServer)
               .RequireRateLimiting(NodeAuthRateLimits.McpPolicy);

            // The inbound OpenAI-compatible model proxy, hand-mapped like MapMcp and for the same reasons; its paths MUST stay inside /api/local/v1. The LocalModelProxy
            // policy accepts ONLY the model-proxy API key scheme, never the operator's JWT or the MCP key. See docs/wiki/09-api-and-hubs.md ("Notable non-typed routes").
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

            // The inbound EXTERNAL integration API, hand-mapped like MapMcp and the model proxy and for the same reasons; its paths MUST stay inside /api/local/v1. The
            // IntegrationApi policy accepts ONLY the integration key scheme. See docs/wiki/09-api-and-hubs.md ("Notable non-typed routes").
            var integrationRoutePrefix = $"/{LocalApiRoutes.Prefix}/";

            // Composition-time value with ONE authority: endpoint metadata is baked at map time, so the cap must not come from an IOptions<IntegrationOptions> resolved per
            // request, which would be a second source that can disagree with what the route already carries. From configuration, the same shape the permit constants use.
            var integrationMaxRequestBodyBytes = app.Configuration.GetValue($"{IntegrationOptions.Section}:{nameof(IntegrationOptions.MaxRequestBodyBytes)}",
                defaultValue: 1024L * 1024L);

            app.MapPost(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.Invoke,
                   static (HttpContext context, IntegrationApiHandler handler) => handler.InvokeAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
               .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy)
               .WithMetadata(new IntegrationRequestSizeLimit(integrationMaxRequestBodyBytes));
            app.MapGet(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.ExecutionById,
                   static (HttpContext context, IntegrationApiHandler handler) => handler.GetExecutionAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
               .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);
            app.MapGet(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.ExecutionEvents,
                   static (HttpContext context, IntegrationApiHandler handler) => handler.GetExecutionEventsAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
               .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);
            app.MapPost(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.ExecutionCancel,
                   static (HttpContext context, IntegrationApiHandler handler) => handler.CancelExecutionAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
               .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);
            app.MapGet(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.SessionById,
                   static (HttpContext context, IntegrationApiHandler handler) => handler.GetSessionAsync(context))
               .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
               .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);

            if (!app.Environment.IsProduction())
            {
                app.MapScalarApiReference("/scalar", static settings =>
                {
                    settings.OpenApiRoutePattern = "/openapi/local/{documentName}/{documentName}.json";

                    settings.AddDocument("v1");

                    settings.AddPreferredSecuritySchemes("Bearer");
                }).AllowAnonymous();
            }

            // The login page has to load before anyone can hold a token, so the SPA shell opts out of the FallbackPolicy explicitly; static assets are served by
            // UseStaticFiles ahead of routing and are unaffected. The marker is what the middleware just after UseRouting matches on to keep the shell off the API prefix.
            app.MapFallbackToFile("index.html").AllowAnonymous().WithMetadata(new SpaFallbackMarker());

            // Desktop mode only: install the console-close graceful-stop triggers and the on-started browser launch. Off the flag this is never reached, so no signal
            // handler or P/Invoke is installed. The lifecycle is rooted for the app's lifetime through the lifetime token registration and disposes with the host.
            if (isLocalMode)
            {
                ActivateDesktopLifecycle(app, launchMode, DesktopLaunch.HasNoBrowserFlag(args));
            }


            return new ProgramStartResult { App = app, ExitCode = 0 };
        }

        /// <summary>
        ///     Resolves the container bridge's listener and adds it to the host's bind URLs, or returns
        ///     <see langword="null" /> when the bridge must not start. Never throws.
        /// </summary>
        /// <remarks>
        ///     A node with no usable interface still boots, without a bridge. The bridge is APPENDED to the hosting
        ///     URLs rather than declared through <c>ConfigureKestrel(o =&gt; o.Listen(...))</c>, and that is not a
        ///     style choice: an explicit Kestrel endpoint OVERRIDES the addresses a host was given, so a Listen call
        ///     here would silently drop the loopback listener desktop mode and Aspire both configure through UseUrls,
        ///     leaving the bridge as the only listener the node has. Both binds travel through the same mechanism.
        /// </remarks>
        private static ResolvedContainerBridgeEndpoint? ResolveContainerBridgeEndpoint(WebApplicationBuilder builder)
        {
            var options = builder.Configuration.GetSection(ContainerBridgeOptions.SectionName).Get<ContainerBridgeOptions>()
                          ?? new ContainerBridgeOptions();
            if (!options.Enabled)
            {
                return null;
            }

            var hostingUrls = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey);
            if (string.IsNullOrWhiteSpace(hostingUrls))
            {
                // Nothing to append to, and appending would make the bridge the node's ONLY listener, so refusing it is the fail-closed answer. Debug, not Warning: every
                // supported launch configures bind URLs, so the host reaching this is the in-memory TestServer, which has no listener for a container to reach anyway.
                Log.Debug("The container bridge is enabled but the host has no configured bind URLs to extend, so it was not opened.");
                return null;
            }

            var endpoint = ContainerBridgeEndpointResolver.Resolve(options, ContainerBridgeEndpointResolver.HostRunsDockerDesktop());
            if (endpoint is null)
            {
                Log.Warning("The container bridge is enabled but no usable host network interface was found (configured bind address: {BindAddress}), "
                            + "so it was not opened. Application containers will not reach this node's inference surface.",
                    options.BindAddress ?? "auto-detect");
                return null;
            }

            var urls = hostingUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Before the URL is appended, two things this machine has to agree to. Both refuse the BRIDGE and keep the node, because a bridge URL that cannot bind fails
            // the whole host: it travels in the same bind list as the loopback listener, and that surface is the one that must survive.
            if (ContainerBridgeListenerProbe.CollidesWithHostingUrls(urls, endpoint.Port))
            {
                Log.Warning("The container bridge is enabled but its port {Port} is already one of this node's own bind URLs ({HostingUrls}), "
                            + "so it was not opened: the node's own requests would arrive on a port the bridge claims. "
                            + "Give the bridge a different '{SectionName}:{PortSetting}' or move the node's listener.",
                    endpoint.Port, hostingUrls, ContainerBridgeOptions.SectionName, nameof(ContainerBridgeOptions.Port));
                return null;
            }

            if (!ContainerBridgeListenerProbe.IsPortAvailable(endpoint.BindAddress, endpoint.Port))
            {
                Log.Warning("The container bridge is enabled but {BindAddress}:{Port} could not be bound, so it was not opened and this node "
                            + "has no bridge. The usual cause is a SECOND node on this machine: the bridge port is a fixed default, because a "
                            + "container is given the endpoint when it is created and must find the same port after a restart. Set "
                            + "'{SectionName}:{PortSetting}' to a free port on the node that should have one.",
                    endpoint.BindAddress, endpoint.Port, ContainerBridgeOptions.SectionName, nameof(ContainerBridgeOptions.Port));
                return null;
            }

            builder.WebHost.UseUrls([.. urls, endpoint.ListenerUrl]);

            // In the SAME place, because a listener without this is a listener nothing can reach: host filtering runs
            // ahead of every middleware the composition root registers and refuses the bridge's own Host header.
            ContainerBridgePipeline.AllowBridgeHost(builder, endpoint);

            Log.Information("The container bridge listens on {ListenerUrl}; application containers reach it at {ContainerFacingEndpoint}. "
                            + "It is the node's only non-loopback listener and refuses every peer that is not this computer.",
                endpoint.ListenerUrl, endpoint.ContainerFacingEndpoint);

            return endpoint;
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

        public TextWriter? StandardOutput { get; init; }

        public TextWriter? StandardError { get; init; }

        public Action? BeforeOneShotCommand { get; init; }

        public Func<HttpClient>? StatusHttpClientFactory { get; init; }
    }

    /// <summary>
    ///     Result of <see cref="Program.CreateAppAsync" />: the built app, or a null app plus the process exit code
    ///     when a CLI early-exit path handled the invocation.
    /// </summary>
    public sealed class ProgramStartResult
    {
        public required WebApplication? App { get; init; }

        public required int ExitCode { get; init; }
    }

    /// <summary>
    ///     Projects a readiness <see cref="HealthReport" /> into the <c>/health/ready</c> JSON payload.
    /// </summary>
    /// <remarks>
    ///     Each check reports its own status, description and structured reason data, so a Degraded worker that still
    ///     returns HTTP 200 — it is serving local inference — is nonetheless distinguishable by an inspecting operator
    ///     or dashboard.
    /// </remarks>
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
