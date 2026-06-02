namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using Quartz;

/// <summary>
///     Quartz job that fires a scheduled definition, allowing overlapping executions of the same definition. The
///     management service  selects this job for definitions whose <c>PreventOverlap == false</c>; the
///     <see cref="NonOverlappingSchedulerDispatchJob" /> variant is used when overlap must be prevented. Kept thin:
///     all guard rails and handler invocation live in <see cref="ISchedulerDispatchExecutor" />.
/// </summary>
internal sealed class SchedulerDispatchJob(
    ISchedulerDispatchExecutor dispatchExecutor,
    ILogger<SchedulerDispatchJob> logger) : IJob
{
    private readonly ISchedulerDispatchExecutor _dispatchExecutor =
        dispatchExecutor ?? throw new ArgumentNullException(nameof(dispatchExecutor));

    private readonly ILogger<SchedulerDispatchJob> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public Task Execute(IJobExecutionContext context) =>
        SchedulerDispatchJobRunner.RunAsync(_dispatchExecutor, _logger, context);
}
