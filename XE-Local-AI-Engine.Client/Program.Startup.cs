namespace XE_Local_AI_Engine.Client;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;
using Serilog;
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Shutdown;

public sealed partial class Program
{
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

    private static void ActivateDesktopLifecycle(WebApplication app, LaunchMode launchMode, bool noBrowserRequested)
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
        var desktopLifecycle = new DesktopLifecycle(lifetime,
            server,
            logger,
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
