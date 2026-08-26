namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
    private static readonly DateTimeOffset Now = new(year: 2026, month: 6, day: 1, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    // ──────────────────────────────────────────────────────────────────────
    // Success → Succeeded with completion + duration
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerSucceeds_RecordsSucceededRun()
    {
        var handler = new ConfigurableHandler((_, _) => Task.CompletedTask);
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-success", Now, Now, CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.InvocationCount, "Handler must run exactly once.");
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

    [Test]
    public async Task DispatchAsync_WhenTriggeredManually_RecordsManualOnTheRunAndTheContext()
    {
        ScheduledRunTrigger? observed = null;
        var handler = new ConfigurableHandler((ctx, _) =>
        {
            observed = ctx.TriggeredBy;
            return Task.CompletedTask;
        });
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-manual", Now, Now, CancellationToken.None, parameterOverrides: null, ScheduledRunTrigger.Manual);

        await runStore.Received(1).UpsertByFireInstanceAsync(Arg.Is<ScheduledJobRunInput>(i => i.TriggeredBy == ScheduledRunTrigger.Manual),
            Arg.Any<CancellationToken>());
        AssertEx.Equal(ScheduledRunTrigger.Manual, observed!.Value);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Success summary: the handler's own sentence is persisted; its absence keeps the generic constant
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenHandlerRecordsSummary_PersistsThatSummaryOnTheRun()
    {
        const string handlerSummary = "Benchmark project 2222: 3/4 cell(s) enqueued, 6 run(s) created.";
        var handler = new ConfigurableHandler((ctx, _) =>
        {
            ctx.Summary = handlerSummary;
            return Task.CompletedTask;
        });
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-summary", Now, Now, CancellationToken.None);

        await runStore.Received(1).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Succeeded,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Is<string?>(summary => summary == handlerSummary),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchAsync_WhenHandlerRecordsNoSummary_PersistsTheGenericCompletedConstant()
    {
        // Left untouched (the common case) and set to whitespace both fall back to the constant.
        var handler = new ConfigurableHandler((ctx, _) =>
        {
            ctx.Summary = ctx.FireInstanceId == "fire-blank-summary" ? "   " : null;
            return Task.CompletedTask;
        });
        var (executor, runStore, _, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-no-summary", Now, Now, CancellationToken.None);
        await executor.DispatchAsync(JobId, "fire-blank-summary", Now, Now, CancellationToken.None);

        await runStore.Received(2).UpdateLifecycleAsync(RunId,
            ScheduledRunStatus.Succeeded,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Is<string?>(summary => summary == "Completed."),
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
                .Returns(Task.FromResult<ScheduledJobRunRecord?>(RunRecord(ScheduledRunStatus.Running, cancellationRequestedAtUtc: 123L)));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executor.DispatchAsync(JobId, "fire-cancel", scheduledFireTimeUtc: null, Now, cts.Token));

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

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executor.DispatchAsync(JobId, "fire-timeout", scheduledFireTimeUtc: null, Now, cts.Token));

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
            await ctx.ReportProgressAsync!("indexing models", arg2: 50, ct);
        });
        var (executor, _, eventStore, _) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-progress", scheduledFireTimeUtc: null, Now, CancellationToken.None);

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

        AssertEx.Equal(expected: 0, handler.InvocationCount, "A terminal re-fire must not re-run the handler.");
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
        var handler = new ConfigurableHandler(async (ctx, ct) => await ctx.ReportProgressAsync!("step", arg2: 25, ct));
        var (executor, _, _, publisher) = CreateExecutor(handler, ScheduledRunStatus.Running);

        await executor.DispatchAsync(JobId, "fire-publish-progress", scheduledFireTimeUtc: null, Now, CancellationToken.None);

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
            Description: null,
            Enabled: true,
            ScheduleKind.OneShot,
            CronExpression: null,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            ParameterJson: null,
            ScheduledJobCreator.User,
            CreatedAtUtc: 0L,
            UpdatedAtUtc: 0L,
            DisabledAtUtc: null,
            DeletedAtUtc: null);
    }

    private static ScheduledJobRunRecord RunRecord(ScheduledRunStatus status, long? cancellationRequestedAtUtc = null)
    {
        return new ScheduledJobRunRecord(RunId,
            JobId,
            ConfigurableHandler.Id,
            "fire",
            ScheduledRunTrigger.Schedule,
            status,
            ScheduledFireTimeUtc: null,
            Now.ToUnixTimeMilliseconds(),
            CompletedAtUtc: null,
            DurationMs: null,
            Summary: null,
            DetailsJson: null,
            ErrorMessage: null,
            ErrorDetails: null,
            cancellationRequestedAtUtc,
            CreatedAtUtc: 1L);
    }

    private static ScheduledJobRunEventRecord EventRecord(ScheduledJobRunEventInput input)
    {
        return new ScheduledJobRunEventRecord(Guid.NewGuid(), input.RunId, input.Sequence, input.Level, input.Message, input.DataJson, OccurredAtUtc: 1L);
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
            ParameterSchema: null,
            DefaultParameters: null,
            [ScheduleKind.OneShot, ScheduleKind.Cron],
            ScheduleKind.OneShot,
            SchedulerMisfirePolicy.SkipMissed,
            DefaultMaxRuntimeSeconds: null,
            AllowManualTrigger: true);

        public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return _body(context, cancellationToken);
        }
    }
}
