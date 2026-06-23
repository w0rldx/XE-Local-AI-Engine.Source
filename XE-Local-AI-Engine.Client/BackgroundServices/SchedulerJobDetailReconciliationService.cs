namespace XE_Local_AI_Engine.Client.BackgroundServices;

using System.Reflection;
using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Startup self-heal for persisted Quartz job details whose stored <c>JOB_CLASS_NAME</c> no longer resolves. When the
///     dispatch <see cref="Quartz.IJob" /> types move namespaces, every <c>QRTZ_JOB_DETAILS</c> row written by an older
///     build still references the old type name, so Quartz fails to load the job (manual trigger 500s, recurring fires
///     fault). This hosted service re-adds every enabled, non-deleted definition's durable JobDetail with
///     <c>replace=true</c> so the class name refreshes to the current <c>typeof(...)</c> value — covering recurring jobs
///     that are never manually triggered. It never changes a trigger's schedule and never fires a job.
///     <para>
///         <b>Best-effort.</b> A node must still start even if reconciliation fails (e.g. a transient DB error), so the
///         expected failures are logged and swallowed; manual triggering still self-heals on demand. Registered in the
///         Client host AFTER <c>AddNodeScheduler</c> so the scheduler factory/job store are available when this runs.
///     </para>
/// </summary>
public sealed class SchedulerJobDetailReconciliationService : IHostedService
{
    private readonly ILogger<SchedulerJobDetailReconciliationService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public SchedulerJobDetailReconciliationService(IServiceScopeFactory scopeFactory,
        ILogger<SchedulerJobDetailReconciliationService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var managementService = scope.ServiceProvider.GetRequiredService<IScheduledJobManagementService>();

            _ = await managementService.ReconcileDurableJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to reconcile.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or SchedulerException
                                       or TypeLoadException or ReflectionTypeLoadException)
        {
            // Reconciliation is best-effort: a node must start even if the heal fails. Manual triggering re-heals on
            // demand, and the next startup re-attempts once the underlying issue clears.
            _logger.LogWarning(ex,
                "Scheduler job-detail reconciliation failed at startup; stale persisted jobs may not heal until the next start or a manual trigger.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
