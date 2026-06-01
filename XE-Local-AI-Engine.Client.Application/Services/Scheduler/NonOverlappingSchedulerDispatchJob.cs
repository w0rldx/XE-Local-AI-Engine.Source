namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using Quartz;

/// <summary>
///     Quartz job that fires a scheduled definition, preventing concurrent executions of the same definition.
///     <see cref="DisallowConcurrentExecutionAttribute" /> is keyed per <c>JobKey</c>, so distinct definitions still
///     run independently — only re-entrant fires of the same definition are serialized. The management service
///     (Marker 3) selects this job for definitions whose <c>PreventOverlap == true</c>; otherwise it uses
///     <see cref="SchedulerDispatchJob" />. Kept thin: all guard rails and handler invocation live in
///     <see cref="ISchedulerDispatchExecutor" />.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class NonOverlappingSchedulerDispatchJob(
    ISchedulerDispatchExecutor dispatchExecutor,
    ILogger<NonOverlappingSchedulerDispatchJob> logger) : IJob
{
    private readonly ISchedulerDispatchExecutor _dispatchExecutor =
        dispatchExecutor ?? throw new ArgumentNullException(nameof(dispatchExecutor));

    private readonly ILogger<NonOverlappingSchedulerDispatchJob> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public Task Execute(IJobExecutionContext context) =>
        SchedulerDispatchJobRunner.RunAsync(_dispatchExecutor, _logger, context);
}
