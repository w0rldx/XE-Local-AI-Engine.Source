namespace XE_Local_AI_Engine.Tests.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Test-only handler for the <c>test.echo</c> template. Records every invocation so unit and integration tests
///     can assert that the dispatcher called (or skipped) execution. Not registered in production DI.
/// </summary>
internal sealed class TestEchoScheduledJobHandler : IScheduledJobHandler
{
    public const string Id = "test.echo";

    private readonly List<ScheduledJobExecutionContext> _captured = [];

    /// <summary>Number of times <see cref="ExecuteAsync" /> has been called.</summary>
    public int InvocationCount => _captured.Count;

    /// <summary>All captured contexts in invocation order.</summary>
    public IReadOnlyList<ScheduledJobExecutionContext> CapturedContexts => _captured;

    /// <summary>The most-recently captured context, or <see langword="null" /> if never invoked.</summary>
    public ScheduledJobExecutionContext? LastContext => _captured.Count > 0 ? _captured[^1] : null;

    public string TemplateId => Id;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new(Id,
        "Echo (test)",
        "No-op test handler that records invocations.",
        null,
        null,
        [ScheduleKind.OneShot, ScheduleKind.Cron],
        ScheduleKind.OneShot,
        SchedulerMisfirePolicy.SkipMissed,
        null,
        true);

    public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _captured.Add(context);
        return Task.CompletedTask;
    }
}
