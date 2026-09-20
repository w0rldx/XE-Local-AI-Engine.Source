namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using System.Reflection;
using Quartz;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Startup self-heal for persisted Quartz job details whose stored <c>JOB_CLASS_NAME</c> no longer resolves.
/// </summary>
/// <remarks>
///     A <c>QRTZ_JOB_DETAILS</c> row written by an older build still names the dispatch <see cref="Quartz.IJob" />
///     type's old namespace, and Quartz then fails to load the job (manual trigger 500s, recurring fires fault). This
///     hosted service re-adds every enabled, non-deleted definition's durable JobDetail with <c>replace=true</c>, so
///     the class name refreshes, covering recurring jobs nobody triggers manually; it never changes a schedule or
///     fires a job. Best-effort: failures are logged and swallowed, since a node must start regardless.
/// </remarks>
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

            _ = await managementService.ReconcileDurableJobsAsync(cancellationToken);
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
