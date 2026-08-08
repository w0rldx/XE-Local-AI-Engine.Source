namespace XE_Local_AI_Engine.Client.Persistence.Tests.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Test-only handler for the <c>test.echo</c> template. Records every invocation.
///     Registered in integration test DI only — not in production.
/// </summary>
internal sealed class TestEchoScheduledJobHandler : IScheduledJobHandler
{
    public const string Id = "test.echo";

    private readonly List<ScheduledJobExecutionContext> _captured = [];

    public int InvocationCount => _captured.Count;

    public string TemplateId => Id;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new(Id,
        "Echo (test)",
        "No-op test handler that records invocations.",
        ParameterSchema: null,
        DefaultParameters: null,
        [ScheduleKind.OneShot, ScheduleKind.Cron, ScheduleKind.Manual],
        ScheduleKind.OneShot,
        SchedulerMisfirePolicy.SkipMissed,
        DefaultMaxRuntimeSeconds: null,
        AllowManualTrigger: true);

    public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _captured.Add(context);
        return Task.CompletedTask;
    }
}
