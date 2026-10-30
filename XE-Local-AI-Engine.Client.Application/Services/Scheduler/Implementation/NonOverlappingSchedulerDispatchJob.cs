namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using Microsoft.Extensions.Options;
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
    private readonly TimeSpan _defaultMaxRuntime;
    private readonly TimeProvider _timeProvider;

    public NonOverlappingSchedulerDispatchJob(ISchedulerDispatchExecutor dispatchExecutor,
        IOptions<SchedulerOptions> options,
        TimeProvider timeProvider,
        ILogger<NonOverlappingSchedulerDispatchJob> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatchExecutor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatchExecutor = dispatchExecutor;
        _defaultMaxRuntime = TimeSpan.FromMinutes(options.Value.DefaultMaxRuntimeMinutes);
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        await SchedulerDispatchJobRunner.RunAsync(_dispatchExecutor, _logger, context, _defaultMaxRuntime, _timeProvider, cancellationToken);
    }
}
