namespace XE_Local_AI_Engine.Client;

using Microsoft.AspNetCore.Hosting.Server;
using Serilog;
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

public sealed partial class Program
{
    /// <summary>
    ///     Reads every saved Open Canvas workflow BEFORE migrations, because the <c>DropCanvasWorkflows</c> migration
    ///     removes the table they live in and no migration can decrypt the graph blob.
    /// </summary>
    /// <remarks>
    ///     The encrypted source is staged durably before migrations. A failed read stops startup before any drop.
    /// </remarks>
    private static async Task<CanvasWorkflowImportSnapshot> ReadPendingCanvasWorkflowsAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CanvasWorkflowImport));

            // CancellationToken.None: the startup path has no token — it runs before the host (and its
            // ApplicationStopping) exists, and abandoning it half-done would leave the node partially migrated.
            return await CanvasWorkflowImport.ReadAsync(dbContext, logger, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Saved Open Canvas workflows could not be read; startup stopped before migrations.");
            throw;
        }
    }

    /// <summary>
    ///     Writes the canvases the pre-migration read took, now that the Graph Workflow tables exist. Runs regardless
    ///     of <c>GraphWorkflows:Enabled</c>: an operator who never turns the feature on must not lose their canvases.
    /// </summary>
    private static async Task ImportCanvasWorkflowsAsync(IServiceProvider services, CanvasWorkflowImportSnapshot pending)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pending);

        try
        {
            await using var scope = services.CreateAsyncScope();
            var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
            var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CanvasWorkflowImport));

            // CancellationToken.None: no token exists on the startup path (see ReadPendingCanvasWorkflowsAsync).
            await CanvasWorkflowImport.ImportAsync(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>(), definitions, store, pending, logger, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Saved Open Canvas workflows could not be imported; encrypted recovery data is retained and startup stopped.");
            throw;
        }
    }

    private static async Task ApplyNodeChatMigrationsAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        // Snapshot the node database before applying pending migrations, in the same scope. Best-effort: a backup failure is logged and swallowed inside the service, so
        // it can never block migration or brick startup. CancellationToken.None throughout — no token exists before the host is built, and a half-applied migration is worse.
        var backupService = scope.ServiceProvider.GetRequiredService<INodeDbBackupService>();
        await backupService.BackupBeforeMigrationAsync(CancellationToken.None);

        var migrationService = scope.ServiceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();
        await migrationService.MigrateAsync(CancellationToken.None);
    }

    private static async Task ApplyNodeIdentityMigrationsAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var initializationService = scope.ServiceProvider.GetRequiredService<NodeIdentityInitializationService>();

        // CancellationToken.None: no token exists on the startup/migration path (see ApplyNodeChatMigrationsAsync).
        await initializationService.MigrateAndSeedAsync(CancellationToken.None);
    }

    private static async Task RecoverInterruptedNodeChatMessagesAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var recoveryService = scope.ServiceProvider.GetRequiredService<NodeChatRestartRecoveryService>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        // CancellationToken.None: no token exists on the startup path.
        await recoveryService.RecoverInterruptedMessagesAsync(timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None);
    }

    private static async Task ReconcileStaleScheduledRunsAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A previous process may have died mid-run, leaving Queued/Running rows whose in-memory cancellation registry is gone. Reconcile them to a sanitized terminal
        // state BEFORE the Quartz hosted service starts firing recovery work, so the history never shows a run stuck Running forever. A no-op with no history.
        await using var scope = services.CreateAsyncScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IScheduledJobRunStore>();

        // CancellationToken.None: no token exists on the startup path.
        var reconciledCount = await runStore.MarkStaleActiveRunsAsync(ScheduledRunStatus.Failed,
            "Run was interrupted by a node restart and reconciled at startup.",
            CancellationToken.None);

        if (reconciledCount > 0)
        {
            Log.Information("Reconciled {ReconciledCount} stale scheduled job run(s) at startup.", reconciledCount);
        }
    }

    private static void ActivateDesktopLifecycle(WebApplication app, LaunchMode launchMode, bool noBrowserRequested)
    {
        ArgumentNullException.ThrowIfNull(app);

        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var server = app.Services.GetRequiredService<IServer>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<DesktopLifecycle>();
        var timeProvider = app.Services.GetRequiredService<TimeProvider>();

        // The per-user data dir the desktop branch set (see Program top); the lifecycle persists the bound loopback port
        // there post-start so the next launch can re-bind it for a stable browser origin.
        var desktopDataDirectory = app.Configuration[DesktopBootstrap.NodeDataDirectoryKey];

        // Ownership is transferred to the host lifetime: the instance lives for the app's lifetime, rooting the native console-ctrl delegate held inside it, and is
        // disposed when the host stops. CA2000 cannot see the deferred disposal through the lifetime registration, so it is suppressed with that justification.
#pragma warning disable CA2000 // Disposal is deferred to and owned by ApplicationStopped below.
        var desktopLifecycle = new DesktopLifecycle(lifetime,
            server,
            logger,
            timeProvider,
            desktopDataDirectory,
            suppressBrowser: DesktopLaunch.ShouldSuppressBrowser(launchMode, noBrowserRequested),
            version: AddNodeMcpServerExtensions.ServerVersion);
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
}
