using Serilog;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Persistence;

// IMPORTANT: Velopack hook dispatch must be the first executable statement and stay outside the top-level catch, because hook-driven exits must not be
// logged as startup failures. The Windows distribution uses the adjacent C# launcher as its managed executable locator; Linux and dev keep the default.
FrameworkDependentVelopackBootstrap.Run(args);
ProcessCrashHooks.Register();

// Once, before any child exists: children inherit the error mode, so one that fails to initialise exits instead of hanging on a dialog.
if (OperatingSystem.IsWindows())
{
    WindowsErrorMode.SuppressHardErrorDialogs();
}

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
catch (DesktopDataDirectoryException ex)
{
    // An unusable data directory (not writable, not creatable) is the operator's to fix, not a crash: name it and exit 10.
    Log.Fatal(ex, "The data directory is unusable; startup stopped.");
    await Console.Error.WriteLineAsync(ex.SafeDiagnostic);
    return XE_Local_AI_Engine.Client.Program.DataDirectoryUnusableExitCode;
}
catch (NodeKeyCustodyException ex)
{
    // Raised by the node-key check while the host starts: a refusal with an operator-facing message, not a crash.
    Log.Fatal(ex.InnerException, "The node key cannot open the database; startup stopped: {Reason}", ex.Message);
    await Console.Error.WriteLineAsync(ex.Message);
    return XE_Local_AI_Engine.Client.Program.NodeKeyCustodyExitCode;
}
catch (Exception ex) when (ApplicationControlBlock.TryDescribe(ex, out var blockedFile))
{
    // Smart App Control or an application control policy refused an unsigned engine file (a Quartz assembly, first): not a crash the operator can
    // read from a stack trace. Name the file on stderr for the desktop shell, which maps exit 11 and this line to its own dialog.
    if (!XE_Local_AI_Engine.Client.Program.StartupLoggerReady)
    {
        await StartupCrashLog.RecordAsync("Windows application control blocked an engine file during early startup", ex, CancellationToken.None);
    }

    Log.Fatal(ex, "Windows application control blocked {BlockedFile}; startup stopped.", blockedFile);
    await Console.Error.WriteLineAsync(ApplicationControlBlock.Describe(blockedFile));
    return XE_Local_AI_Engine.Client.Program.ApplicationControlBlockedExitCode;
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
    using System.Diagnostics.CodeAnalysis;
    using Serilog;
    using XE_Local_AI_Engine.Client.Endpoints.Common;
    using XE_Local_AI_Engine.Client.Hosting;
    using XE_Local_AI_Engine.Client.Hosting.Pipeline;
    using XE_Local_AI_Engine.Client.Hubs;
    using XE_Local_AI_Engine.Client.Middleware;
    using XE_Local_AI_Engine.Client.Services.Auth;
    using XE_Local_AI_Engine.Client.Services.Containers.Bridge;

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
                var exitCode = 1;
                if (exception is DesktopDataDirectoryException dataDirectoryException)
                {
                    await standardError.WriteLineAsync(dataDirectoryException.SafeDiagnostic);
                    exitCode = DataDirectoryUnusableExitCode;
                }

                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = exitCode
                };
            }
        }

        /// <summary>
        ///     The node request pipeline in execution order: every <c>Use*</c> and <c>Map*</c> call of the host. The
        ///     order is load-bearing; each step's comment, here or in its module, says why it sits where it does.
        /// </summary>
        private static void ConfigureRequestPipeline(WebApplication app,
            ResolvedContainerBridgeEndpoint? bridgeEndpoint,
            bool isLocalMode,
            bool isDevelopmentModeEnabled)
        {
            // FIRST middleware: once the host is stopping, every new request (hub negotiate and WebSocket upgrade included)
            // gets 503, or a late hub connection holds the shutdown until HostOptions.ShutdownTimeout.
            app.UseShutdownRequestGuard(app.Lifetime);

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
            app.UseNodeResponseHeaders();

            app.UseNodeTransportSecurity(isLocalMode);

            app.UseAntiforgery();

            app.UseMiddleware<NativeDesktopDocumentPolicy>();
            // One instance per pipeline: the static files and the fallback share one provider and its content-stamp cache.
            var spaFiles = SpaStaticFileOptions.Create(app.Environment.WebRootFileProvider);
            app.UseContentValidatedStaticFiles(spaFiles);
            app.MapNodeHealthChecks();

            // The feature switches (Development, work sessions, dev and graph workflows, transcription, external apps) answer 404 per request from node
            // settings, ahead of local API security and authentication so a switched-off feature cannot be probed by status code.
            app.UseMiddleware<FeatureSwitchMiddleware>();

            app.UseMiddleware<LocalApiSecurityMiddleware>();
            app.UseRouting();
            app.UseSpaFallbackApiGuard();

            // Skipped in the Testing environment, where the permit limits are relaxed to non-limits anyway: the middleware's undisposed replenishment timer GC-roots the
            // whole host. See docs/wiki/11-hosting-and-deployment.md ("The node host pipeline (`Program.cs`)") and docs/agent-knowledge.md §1.
            if (!app.Environment.IsEnvironment("Testing"))
            {
                app.UseRateLimiter();
            }

            app.UseDevOpenApiDocument();

            // Wrong verbs and bodies on the MCP endpoint answer 405/415 here, before authentication, instead of the JWT fallback's 401.
            app.UseMcpEndpointMethodGuard(new PathString($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerEndpoint}"));
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseNodeFastEndpoints(isLocalMode);
            MapNodeHubs(app, isDevelopmentModeEnabled);
            app.MapNodeInboundApis();
            app.MapDevApiReference();
            app.MapSpaFallback(spaFiles);
        }

        /// <summary>
        ///     Maps every SignalR hub behind the Operator policy. Kept in this file: the SignalR proxy-path drift guards
        ///     text-walk <c>Program.cs</c> for its <c>MapHub</c> calls.
        /// </summary>
        private static void MapNodeHubs(WebApplication app, bool isDevelopmentModeEnabled)
        {
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
            app.MapHub<RuntimeResidencyHub>(LocalApiRoutes.ModelFit.ResidencyHub)
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

            // Mapped unconditionally, like the work-session hub: the DevWorkflows switch is enforced by FeatureSwitchMiddleware
            // above, which answers 404 for the whole prefix — including this path.
            app.MapHub<DevWorkflowRunHub>(LocalApiRoutes.DevelopmentWorkflows.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            // Same posture for graph workflows: the GraphWorkflows switch is enforced by FeatureSwitchMiddleware
            // above, which answers 404 for the whole prefix — this path included.
            app.MapHub<GraphWorkflowRunHub>(LocalApiRoutes.GraphWorkflows.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            // And for external apps: the ExternalApps switch is enforced by FeatureSwitchMiddleware above, which
            // answers 404 for the whole prefix — this path and its negotiate included.
            app.MapHub<ExternalAppHub>(LocalApiRoutes.ExternalApps.Hub)
               .RequireAuthorization(NodeAuthorizationPolicies.Operator);

            // Development's hub follows the startup value, like its endpoints and services; a stored change applies after a restart.
            if (isDevelopmentModeEnabled)
            {
                app.MapHub<DevelopmentAttemptHub>(LocalApiRoutes.Development.Hub)
                   .RequireAuthorization(NodeAuthorizationPolicies.Operator);
            }
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
}
