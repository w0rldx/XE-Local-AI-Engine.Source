namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using Quartz;

/// <summary>
///     Quartz job that fires a scheduled definition, preventing concurrent executions of the same definition.
/// </summary>
/// <remarks>
///     <see cref="DisallowConcurrentExecutionAttribute" /> is keyed per <c>JobKey</c>, so distinct definitions still
///     run independently and only re-entrant fires of one definition are serialized. The management service selects
///     this job for definitions that prevent overlap, and <see cref="SchedulerDispatchJob" /> otherwise. Kept thin:
///     all guard rails and handler invocation live in <see cref="ISchedulerDispatchExecutor" />.
/// </remarks>
[DisallowConcurrentExecution]
internal sealed class NonOverlappingSchedulerDispatchJob : IJob
{
    private readonly ISchedulerDispatchExecutor _dispatchExecutor;
    private readonly ILogger<NonOverlappingSchedulerDispatchJob> _logger;

    public NonOverlappingSchedulerDispatchJob(
        ISchedulerDispatchExecutor dispatchExecutor,
        ILogger<NonOverlappingSchedulerDispatchJob> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatchExecutor);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatchExecutor = dispatchExecutor;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context)
    {
        return SchedulerDispatchJobRunner.RunAsync(_dispatchExecutor, _logger, context);
    }
}
