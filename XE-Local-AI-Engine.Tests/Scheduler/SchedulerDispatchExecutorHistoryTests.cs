namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Run-history recording tests for <see cref="SchedulerDispatchExecutor" />. Substitute stores
///     let each lifecycle outcome be asserted at the executor↔store boundary — including the redaction contract (no
///     exception message or stack trace is ever handed to the store) and fire-instance idempotency.
/// </summary>
public sealed class SchedulerDispatchExecutorHistoryTests
{
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ──────────────────────────────────────────────────────────────────────
    // Success → Succeeded with completion + duration
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerSucceeds_RecordsSucceededRun()
    {
        var handler = new ConfigurableHandler((_, _) => Task.CompletedTask);
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-success", Now, Now, CancellationToken.None);

        AssertEx.Equal(1, handler.InvocationCount, "Handler must run exactly once.");
        await runStore.Received(1).UpsertByFireInstanceAsync(Arg.Is<ScheduledJobRunInput>(i => i.Status == ScheduledRunStatus.Running && i.QuartzFireInstanceId == "fire-success"),
            Arg.Any<CancellationToken>());
        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Succeeded,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Handler throws → Failed, sanitized (no secret leaks), not re-thrown
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerThrows_RecordsFailedRunWithSanitizedErrorAndDoesNotRethrow()
    {
        const string secret = "super-secret-password-1234";
        var handler = new ConfigurableHandler((_, _) => throw new InvalidOperationException(secret));
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        // Must NOT throw — the run row is the record of failure; a faulting handler may not fault the scheduler.
        await executor.DispatchAsync(JobId, "fire-fail", Now, Now, CancellationToken.None);

        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Failed,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(message => message != null && !message.Contains(secret, StringComparison.Ordinal)),
            Arg.Is<string?>(details => details == null || !details.Contains(secret, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Handler throws the sanitized marker exception → its message is recorded verbatim
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerThrowsScheduledJobExecutionException_RecordsSanitizedMessageVerbatim()
    {
        const string sanitized = "The approved image is disabled.";
        var handler = new ConfigurableHandler((_, _) => throw new ScheduledJobExecutionException(sanitized));
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-sanitized", Now, Now, CancellationToken.None);

        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Failed,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(message => message == sanitized),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Any other exception type → the generic constant is still recorded (no regression)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerThrowsGenericException_StillRecordsGenericMessage()
    {
        var handler = new ConfigurableHandler((_, _) => throw new InvalidOperationException("raw internal detail"));
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-generic", Now, Now, CancellationToken.None);

        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Failed,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(message => message == "The scheduled job failed during execution."),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cancellation: token tripped + cancel was requested → Cancelled, re-thrown
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenCancelledAndCancellationRequested_RecordsCancelledAndRethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new ConfigurableHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);
        // The dispatcher re-reads the run on cancel; report that cancellation was requested.
        runStore.GetByIdAsync(RunId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ScheduledJobRunRecord?>(RunRecord(ScheduledRunStatus.Running, 123L)));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executor.DispatchAsync(JobId, "fire-cancel", null, Now, cts.Token));

        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Cancelled,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cancellation token tripped without a cancel request → TimedOut (auto-interrupt)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenCancelledWithoutRequest_RecordsTimedOutAndRethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new ConfigurableHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);
        // No cancellation requested → the only token-cancel source is the auto-interrupt max-runtime plugin.
        runStore.GetByIdAsync(RunId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<ScheduledJobRunRecord?>(RunRecord(ScheduledRunStatus.Running)));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executor.DispatchAsync(JobId, "fire-timeout", null, Now, cts.Token));

        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.TimedOut,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Progress callback → run event appended
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerReportsProgress_AppendsProgressEvent()
    {
        var handler = new ConfigurableHandler(async (ctx, ct) =>
        {
            await ctx.ReportProgressAsync!("indexing models", 50, ct);
        });
        var (executor, _, eventStore, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-progress", null, Now, CancellationToken.None);

        await eventStore.Received(1).AddAsync(Arg.Is<ScheduledJobRunEventInput>(e =>
                e.RunId == RunId &&
                e.Level == ScheduledRunEventLevel.Progress &&
                e.Sequence == 1 &&
                e.Message == "indexing models" &&
                e.DataJson != null &&
                e.DataJson.Contains("50", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Idempotency: a re-fire whose run is already terminal skips re-execution
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenFireInstanceAlreadyTerminal_SkipsHandlerAndDoesNotUpdate()
    {
        var handler = new ConfigurableHandler((_, _) => Task.CompletedTask);
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Succeeded);

        await executor.DispatchAsync(JobId, "fire-dup", Now, Now, CancellationToken.None);

        AssertEx.Equal(0, handler.InvocationCount, "A terminal re-fire must not re-run the handler.");
        await runStore.DidNotReceive().UpdateLifecycleAsync(Arg.Any<Guid>(),
            Arg.Any<ScheduledRunStatus>(),
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Realtime: lifecycle transitions publish sanitized run events
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerSucceeds_PublishesRunStartedThenRunCompleted()
    {
        var handler = new ConfigurableHandler((_, _) => Task.CompletedTask);
        var (executor, _, _, publisher) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-publish", Now, Now, CancellationToken.None);

        await publisher.Received(1).PublishRunAsync(Arg.Is<SchedulerRunHubEvent>(e => e.EventType == SchedulerHubEvents.RunStarted && e.RunId == RunId),
            Arg.Any<CancellationToken>());
        await publisher.Received(1).PublishRunAsync(Arg.Is<SchedulerRunHubEvent>(e => e.EventType == SchedulerHubEvents.RunCompleted && e.RunId == RunId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchAsync_WhenHandlerReportsProgress_PublishesProgressEvent()
    {
        var handler = new ConfigurableHandler(async (ctx, ct) => await ctx.ReportProgressAsync!("step", 25, ct));
        var (executor, _, _, publisher) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-publish-progress", null, Now, CancellationToken.None);

        await publisher.Received(1).PublishRunProgressAsync(Arg.Is<SchedulerRunProgressHubEvent>(e =>
                e.EventType == SchedulerHubEvents.RunProgress &&
                e.RunId == RunId &&
                e.Message == "step" &&
                e.Percent == 25),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static (SchedulerDispatchExecutor Executor, IScheduledJobRunStore RunStore, IScheduledJobRunEventStore EventStore, ISchedulerEventPublisher Publisher)
        CreateExecutor(ConfigurableHandler handler, ScheduledRunStatus upsertStatus)
    {
        var definitionStore = Substitute.For<IScheduledJobDefinitionStore>();
        definitionStore.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ScheduledJobDefinitionRecord?>(DefinitionRecord()));

        var runStore = Substitute.For<IScheduledJobRunStore>();
        runStore.UpsertByFireInstanceAsync(Arg.Any<ScheduledJobRunInput>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(RunRecord(upsertStatus)));
        // UpdateLifecycle echoes a record in the requested terminal status so the published event reflects it.
        runStore.UpdateLifecycleAsync(Arg.Any<Guid>(), Arg.Any<ScheduledRunStatus>(), Arg.Any<long?>(), Arg.Any<long?>(),
                    Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult<ScheduledJobRunRecord?>(RunRecord((ScheduledRunStatus)callInfo[1])));

        var eventStore = Substitute.For<IScheduledJobRunEventStore>();
        eventStore.AddAsync(Arg.Any<ScheduledJobRunEventInput>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => Task.FromResult(EventRecord((ScheduledJobRunEventInput)callInfo[0])));

        var publisher = Substitute.For<ISchedulerEventPublisher>();

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = new SchedulerDispatchExecutor(definitionStore,
            registry,
            runStore,
            eventStore,
            publisher,
            TimeProvider.System,
            NullLogger<SchedulerDispatchExecutor>.Instance);

        return (executor, runStore, eventStore, publisher);
    }

    private static ScheduledJobDefinitionRecord DefinitionRecord()
    {
        return new ScheduledJobDefinitionRecord(JobId,
            ConfigurableHandler.Id,
            "History Test Job",
            null,
            true,
            ScheduleKind.OneShot,
            null,
            null,
            null,
            null,
            null,
            "UTC",
            SchedulerMisfirePolicy.Smart,
            false,
            null,
            null,
            ScheduledJobCreator.User,
            0L,
            0L,
            null,
            null);
    }

    private static ScheduledJobRunRecord RunRecord(ScheduledRunStatus status, long? cancellationRequestedAtUtc = null)
    {
        return new ScheduledJobRunRecord(RunId,
            JobId,
            ConfigurableHandler.Id,
            "fire",
            ScheduledRunTrigger.Schedule,
            status,
            null,
            Now.ToUnixTimeMilliseconds(),
            null,
            null,
            null,
            null,
            null,
            null,
            cancellationRequestedAtUtc,
            1L);
    }

    private static ScheduledJobRunEventRecord EventRecord(ScheduledJobRunEventInput input)
    {
        return new ScheduledJobRunEventRecord(Guid.NewGuid(), input.RunId, input.Sequence, input.Level, input.Message, input.DataJson, 1L);
    }

    private sealed class ConfigurableHandler(Func<ScheduledJobExecutionContext, CancellationToken, Task> body)
        : IScheduledJobHandler
    {
        public const string Id = "test.echo";

        private readonly Func<ScheduledJobExecutionContext, CancellationToken, Task> _body = body;

        public int InvocationCount { get; private set; }

        public string TemplateId => Id;

        public ScheduledJobTemplateDescriptor Descriptor { get; } = new(Id,
            "Configurable (test)",
            "Test handler that runs an injected body.",
            null,
            null,
            [ScheduleKind.OneShot, ScheduleKind.Cron],
            ScheduleKind.OneShot,
            SchedulerMisfirePolicy.SkipMissed,
            null,
            true);

        public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return _body(context, cancellationToken);
        }
    }
}
