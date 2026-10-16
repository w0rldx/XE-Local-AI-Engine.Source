namespace XE_Local_AI_Engine.Client
{
    using System.Diagnostics;
    using System.Security.Cryptography;
    using Serilog;
    using Serilog.Extensions.Logging;
    using XE_Local_AI_Engine.Client.Common.Extensions;
    using XE_Local_AI_Engine.Client.Hosting;
    using XE_Local_AI_Engine.Client.Hosting.Vault;
    using XE_Local_AI_Engine.Client.Persistence;
    using XE_Local_AI_Engine.Client.Persistence.Sqlite;
    using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
    using XE_Local_AI_Engine.Client.Services.NodeSettings;
    using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
    using XE_Local_AI_Engine.Client.Services.Persistence;
    using XE_Local_AI_Engine.Client.Services.Vault;

    public sealed partial class Program
    {
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
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = 0
                };
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
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = 2
                };
            }

            var setupRequested = DesktopLaunch.TryGetSetupCommand(args, out var setupCommand, out var setupError);
            if (setupRequested && setupError is not null)
            {
                await standardError.WriteLineAsync(setupError);
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = 2
                };
            }

            if (setupCommand?.PasswordFromEnvironment == true)
            {
                Environment.SetEnvironmentVariable(DesktopLaunch.AdminPasswordEnvironmentVariable, null);
            }

            var mcpKeyRequested = DesktopLaunch.TryGetMcpKeyScope(args, out var mcpKeyScope, out var mcpKeyError);
            if (mcpKeyRequested && mcpKeyError is not null)
            {
                await standardError.WriteLineAsync(mcpKeyError);
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = 3
                };
            }

            // The maintenance commands are one-shots too, so a busy data directory answers them with 4 like setup and --mcp-key.
            var commandRequested = setupRequested || mcpKeyRequested || DesktopLaunch.TryGetResetAdminPassword(args, out _)
                                   || DesktopLaunch.GetKnowledgeDowngradeCommand(args) != KnowledgeDowngradeCommand.None;

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

            // Resolved in the local-data block below and applied after a locked vault (if any) is unwrapped, so the real host
            // re-binds the origin the unlock page used.
            string? desktopDataDirectory = null;
            UnixFileMode? dataDirectoryNarrowedFrom = null;
            string? bindUrl = null;
            var vaultState = VaultState.External;
            var stagedRestore = NodeDbRestoreApplyResult.None;
            if (needsLocalData)
            {
                // Resolve (and create, owner-only) the per-user data dir up front so both the bind below and the config layer share it.
                desktopDataDirectory = DesktopBootstrap.ResolveDataDirectoryPath();
                dataDirectoryNarrowedFrom = DesktopBootstrap.EnsureDirectory(desktopDataDirectory);

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
                    if (commandRequested)
                    {
                        Log.Error("The engine is already running for this data directory. Use the HTTP path on the running "
                                  + "instance, or stop it before running this command.");
                    }

                    await Log.CloseAndFlushAsync();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = commandRequested ? 4 : 1
                    };
                }

                // A present but unreadable node-settings.json would otherwise start the node on default settings, which looks
                // like a fresh install (first-run onboarding) while every stored choice is silently ignored. Stop honestly instead.
                if (await NodeStartupSettings.IsStoredFileUnreadableAsync(desktopDataDirectory, CancellationToken.None))
                {
                    Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);
                    StartupLoggerReady = true;
                    var hint = NodeStartupSettings.DescribeUnreadableFile(desktopDataDirectory);
                    Log.Fatal("{NodeSettingsUnreadableHint}", hint);
                    await standardError.WriteLineAsync(hint);
                    await Log.CloseAndFlushAsync();
                    instanceLease.Dispose();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = DataDirectoryUnusableExitCode
                    };
                }

                // A staged restore swaps the database file while nothing holds it: the lease is ours, and the key and DB are read only below.
                stagedRestore = await ApplyStagedDatabaseRestoreAsync(builder.Configuration, desktopDataDirectory);
                if (stagedRestore.Error is not null)
                {
                    Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);
                    StartupLoggerReady = true;
                    var hint = DescribeStagedRestoreFailure(stagedRestore);
                    Log.Fatal("{DatabaseRecoveryHint}", hint);
                    await standardError.WriteLineAsync(hint);
                    await Log.CloseAndFlushAsync();
                    instanceLease.Dispose();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = DatabaseMigrationFailedExitCode
                    };
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
                        return new ProgramStartResult
                        {
                            App = null,
                            ExitCode = 6
                        };
                    }

#pragma warning disable S5332 // Local mode intentionally binds plain HTTP exclusively on 127.0.0.1; it never leaves the machine.
                    bindUrl = $"http://{DesktopLaunch.LoopbackHost}:{port}";
#pragma warning restore S5332
                }
                else
                {
                    bindUrl = await DesktopPortStore.ResolveBindUrlAsync(desktopDataDirectory, CancellationToken.None);
                }

                // A double-click launch supplies neither the node SQLite connection string nor the operator secret, so fill them from the per-user data directory
                // BEFORE AddServices reads configuration below. Each key is layered in only when absent, so any value already supplied wins.
                try
                {
                    vaultState = DesktopBootstrap.EnsureLocalDataConfiguration(builder.Configuration);
                }
                catch (NodeKeyCustodyException custodyException)
                {
                    Log.Logger = builder.Environment.CreateStartupLogger(builder.Configuration);
                    StartupLoggerReady = true;
                    // The refusal is the operator's message, printed below; a stack trace is logged only for an underlying cause.
                    Log.Fatal(custodyException.InnerException, "The node key cannot open the database; startup stopped: {Reason}", custodyException.Message);
                    await standardError.WriteLineAsync(custodyException.Message);
                    await Log.CloseAndFlushAsync();
                    instanceLease.Dispose();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = NodeKeyCustodyExitCode
                    };
                }
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
            if (dataDirectoryNarrowedFrom is { } previousMode)
            {
                Log.Information("Restricted the data directory {DataDirectory} to its owner; its previous mode {PreviousMode} let other users in.",
                    desktopDataDirectory,
                    Convert.ToString((int)previousMode, 8));
            }

            if (stagedRestore.Applied)
            {
                Log.Information("Restored the node database from snapshot {SnapshotName}; the replaced database was kept at {SetAsidePath}.",
                    stagedRestore.SnapshotName,
                    stagedRestore.SetAsidePath);
            }

            if (stagedRestore.Warning is not null)
            {
                Log.Warning("{RestoreWarning} Marker: {MarkerPath}", stagedRestore.Warning, stagedRestore.MarkerPath);
            }

            // Read once: stdin is consumed, and both the locked-vault unlock below and the reset after migrations need it.
            var resetRequested = DesktopLaunch.TryGetResetAdminPassword(args, out var resetPassword);
            var resetRecoveryCode = resetRequested ? DesktopLaunch.GetRecoveryCode(args) : null;

            // A v2 node.key is the passphrase-wrapped vault (ADR 0018): nothing below may build until it is unwrapped. The lease is
            // already held, so a second launch exits 4 instead of serving a second unlock page.
            VaultUnlockOutcome? vaultUnlock = null;
            if (vaultState == VaultState.Locked)
            {
                var attempt = await UnlockVaultAsync(new VaultUnlockRequestContext
                {
                    Args = args,
                    DataDirectory = desktopDataDirectory!,
                    BindUrl = bindUrl!,
                    Builder = builder,
                    LaunchMode = launchMode,
                    SetupCommand = setupCommand,
                    McpKeyRequested = mcpKeyRequested,
                    ResetRequested = resetRequested,
                    RecoveryCode = resetRecoveryCode,
                    StandardOutput = standardOutput,
                    StandardError = standardError,
                    ParentLost = parentLifetime?.ParentLost
                });
                if (attempt.Outcome is null)
                {
                    instanceLease?.Dispose();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = attempt.ExitCode!.Value
                    };
                }

                vaultUnlock = attempt.Outcome;
                var portRelease = Stopwatch.StartNew();
                if (vaultUnlock.BoundUrl is { } unlockPageUrl)
                {
                    if (!await RebindUnlockPagePortAsync(unlockPageUrl, standardError))
                    {
                        CryptographicOperations.ZeroMemory(vaultUnlock.MasterKey);
                        instanceLease?.Dispose();
                        return new ProgramStartResult
                        {
                            App = null,
                            ExitCode = UnlockPortLostExitCode
                        };
                    }

                    bindUrl = unlockPageUrl;
                }

                InjectUnlockedSecret(builder.Configuration, vaultUnlock);
                if (vaultUnlock.UnlockTicket is { } unlockTicket)
                {
                    // The password unlock's one-time ticket: NodeRefreshEndpoint trades it for a session once when the stored refresh cookie fails.
                    builder.Services.AddSingleton(unlockTicket);
                }
                Log.Information("Vault unlocked; port {Port} free again after {ElapsedMs} ms; building the engine host.",
                    Uri.TryCreate(bindUrl, UriKind.Absolute, out var boundUri) ? boundUri.Port : 0,
                    portRelease.ElapsedMilliseconds);
            }

            var hostBuild = Stopwatch.StartNew();

            if (bindUrl is not null)
            {
                builder.WebHost.UseUrls(bindUrl);
            }

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

            // The switches that change what is registered or bound, read from node-settings.json before the container exists. After the vault step and
            // EnsureLocalDataConfiguration, which layers in the data directory. A test host skips the legacy content-root file: its content root is the source tree.
            NodeStartupSettings startupSettings;
            using (var startupLoggerFactory = new SerilogLoggerFactory(Log.Logger))
            {
                startupSettings = NodeStartupSettings.Read(builder.Configuration,
                    builder.Environment,
                    startupLoggerFactory.CreateLogger<NodeSettingsStore>(),
                    includeLegacyContentRoot: customization is null);
            }

            builder.Services.AddSingleton(startupSettings);
            var isDevelopmentModeEnabled = startupSettings.DevelopmentEnabled;
            var areExternalAppsEnabled = startupSettings.ExternalAppsEnabled;

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

            // The launch mode resolved above, registered once so no service or endpoint re-reads the process arguments.
            var launchContext = new NodeLaunchContext
            {
                IsLocalMode = isLocalMode
            };
            builder.Services.AddSingleton(launchContext);

            builder.AddServices(builder.Configuration, startupSettings);

            // App self-update (Velopack + anonymous public GitHub releases), desktop-mode only: off the flag this registers nothing and the desktop-only endpoints are
            // filtered out of FastEndpoints above. The process args are re-passed on relaunch, so the new version comes back up in desktop mode on the persisted port.
            builder.AddAppUpdate(builder.Configuration,
                launchContext,
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
            Log.Information("Engine host built in {ElapsedMs} ms; applying migrations, then starting hosted services.", hostBuild.ElapsedMilliseconds);

            // SQLite's own error log and the raw-open pragma failures reach this host's logger until it stops. The native hook is process-global and
            // registered once; see docs/wiki/08-data-and-persistence.md ("Connection pragmas").
            var sqliteDiagnostics = NodeSqliteDiagnostics.Attach(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NodeSqliteDiagnostics).FullName!),
                app.Services.GetRequiredService<TimeProvider>());
            app.Lifetime.ApplicationStopped.Register(sqliteDiagnostics.Dispose);

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
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = downgradeExitCode
                };
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

            if (!needsLocalData)
            {
                WarnIgnoredStagedRestore(app.Services);
            }

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
            catch (Exception migrationException) when (IsDatabaseFailure(migrationException))
            {
                // Only a database failure exits 9 with the recovery hint. Invalid options or a DI failure also surface here, the first
                // place the container resolves services, and keep propagating to the top-level catch.
                Log.Error(migrationException, "Database migrations failed to apply.");
                await ReportDatabaseRecoveryAsync(app, migrationException, standardError);
                await Log.CloseAndFlushAsync();
                instanceLease?.Dispose();
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = DatabaseMigrationFailedExitCode
                };
            }

            // Local admin password recovery, handled AFTER identity migrations guarantee the tables and Admin role and BEFORE the web host serves, so it runs against the SAME
            // database the app uses and the single-instance lease has already proved nothing else holds the directory. Resets without the old password, revokes refresh tokens, exits.
            if (vaultUnlock?.ResetPassword is not null)
            {
                await ResetRecoveredAdminPasswordAsync(app.Services, vaultUnlock);
            }

            if (resetRequested)
            {
                var resetExitCode = await ResetAdminPasswordAsync(app.Services, resetPassword, resetRecoveryCode, standardOutput);
                instanceLease?.Dispose();
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = resetExitCode
                };
            }

            if (setupRequested)
            {
                commandContext?.SetStage(OneShotCommandStage.Handler);
                var setupExitCode = await SetupCommandAsync(app.Services, setupCommand!, standardOutput, standardError);
                if (setupExitCode != 0)
                {
                    instanceLease?.Dispose();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = setupExitCode
                    };
                }
            }

            if (mcpKeyRequested)
            {
                commandContext?.SetStage(OneShotCommandStage.Handler);
                var mcpKeyExitCode = await McpKeyCommandAsync(app.Services, mcpKeyScope!.Value, standardOutput, standardError);
                if (mcpKeyExitCode != 0)
                {
                    instanceLease?.Dispose();
                    return new ProgramStartResult
                    {
                        App = null,
                        ExitCode = mcpKeyExitCode
                    };
                }
            }

            if ((setupRequested || mcpKeyRequested) && !DesktopLaunch.HasExplicitLocalModeArgument(args))
            {
                instanceLease?.Dispose();
                return new ProgramStartResult
                {
                    App = null,
                    ExitCode = 0
                };
            }

            await RecoverInterruptedNodeChatMessagesAsync(app.Services);
            await ReconcileStaleScheduledRunsAsync(app.Services);
            ActivateInvocationResumeRegistry(app.Services);

            ConfigureRequestPipeline(app, bridgeEndpoint, isLocalMode, isDevelopmentModeEnabled);

            // Desktop mode only: install the console-close graceful-stop triggers and the on-started browser launch. Off the flag this is never reached, so no signal
            // handler or P/Invoke is installed. The lifecycle is rooted for the app's lifetime through the lifetime token registration and disposes with the host.
            if (isLocalMode)
            {
                // After an unlock the browser is already on the unlock page, on this same origin, and follows to the real host itself.
                ActivateDesktopLifecycle(app, launchMode, DesktopLaunch.HasNoBrowserFlag(args) || vaultUnlock?.BoundUrl is not null);
            }


            return new ProgramStartResult
            {
                App = app,
                ExitCode = 0
            };
        }
    }
}
