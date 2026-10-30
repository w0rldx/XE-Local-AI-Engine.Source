namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using Microsoft.Extensions.Options;
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
    private readonly TimeSpan _defaultMaxRuntime;
    private readonly TimeProvider _timeProvider;

    public SchedulerDispatchJob(ISchedulerDispatchExecutor dispatchExecutor,
        IOptions<SchedulerOptions> options,
        TimeProvider timeProvider,
        ILogger<SchedulerDispatchJob> logger)
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
