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

    public string TemplateId => Id;

    public ScheduledJobTemplateDescriptor Descriptor { get; } = new(
        TemplateId: Id,
        DisplayName: "Echo (test)",
        Description: "No-op test handler that records invocations.",
        ParameterSchema: null,
        DefaultParameters: null,
        SupportedScheduleKinds: [ScheduleKind.OneShot, ScheduleKind.Cron, ScheduleKind.Manual],
        DefaultScheduleKind: ScheduleKind.OneShot,
        DefaultMisfirePolicy: SchedulerMisfirePolicy.SkipMissed,
        DefaultMaxRuntimeSeconds: null,
        AllowManualTrigger: true,
        AllowAgentCreation: false);

    public int InvocationCount => _captured.Count;

    public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _captured.Add(context);
        return Task.CompletedTask;
    }
}
