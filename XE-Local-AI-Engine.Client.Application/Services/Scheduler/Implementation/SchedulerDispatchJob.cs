namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using Quartz;

/// <summary>
///     Quartz job that fires a scheduled definition, allowing overlapping executions of the same definition.
/// </summary>
/// <remarks>
///     The management service selects this job for definitions that allow overlap, and the
///     <see cref="NonOverlappingSchedulerDispatchJob" /> variant when overlap must be prevented. Kept thin: all guard
///     rails and handler invocation live in <see cref="ISchedulerDispatchExecutor" />.
/// </remarks>
internal sealed class SchedulerDispatchJob : IJob
{
    private readonly ISchedulerDispatchExecutor _dispatchExecutor;
    private readonly ILogger<SchedulerDispatchJob> _logger;

    public SchedulerDispatchJob(
        ISchedulerDispatchExecutor dispatchExecutor,
        ILogger<SchedulerDispatchJob> logger)
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
