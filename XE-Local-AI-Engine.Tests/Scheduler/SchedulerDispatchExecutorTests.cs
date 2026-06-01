namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for <see cref="SchedulerDispatchExecutor" />.
///     Guards (null def / disabled / soft-deleted / unknown template) are verified without touching the handler;
///     the happy path confirms the handler is invoked exactly once with the correct context.
/// </summary>
public sealed class SchedulerDispatchExecutorTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ──────────────────────────────────────────────────────────────────────
    // Guard: definition not found
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenDefinitionNotFound_DoesNotInvokeHandlerAndDoesNotThrow()
    {
        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>()).Returns((ScheduledJobDefinitionRecord?)null);

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, "fire-1", scheduledFireTimeUtc: Now, actualFireTimeUtc: Now, CancellationToken.None);

        AssertEx.Equal(0, handler.InvocationCount, "Handler must not be invoked when definition is missing.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Guard: definition disabled
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenDefinitionDisabled_DoesNotInvokeHandlerAndDoesNotThrow()
    {
        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>()).Returns(BuildRecord(enabled: false, deleted: false));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, "fire-2", scheduledFireTimeUtc: Now, actualFireTimeUtc: Now, CancellationToken.None);

        AssertEx.Equal(0, handler.InvocationCount, "Handler must not be invoked for a disabled definition.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Guard: definition soft-deleted (enabled flag is also false on soft-delete,
    //        but the code checks DeletedAtUtc independently; test it with
    //        enabled=true to exercise the deleted branch specifically)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenDefinitionSoftDeleted_DoesNotInvokeHandlerAndDoesNotThrow()
    {
        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        // enabled=true but deleted — the check is `!Enabled || DeletedAtUtc != null`
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: true));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, "fire-3", scheduledFireTimeUtc: Now, actualFireTimeUtc: Now, CancellationToken.None);

        AssertEx.Equal(0, handler.InvocationCount, "Handler must not be invoked for a soft-deleted definition.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Guard: template id has no registered handler
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenTemplateUnknown_DoesNotInvokeHandlerAndDoesNotThrow()
    {
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        // Record references a template that has no handler in the registry.
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, templateId: "unknown.template"));

        // Registry contains only test.echo — "unknown.template" will miss.
        var registry = new ScheduledJobTemplateRegistry([new TestEchoScheduledJobHandler()]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, "fire-4", scheduledFireTimeUtc: Now, actualFireTimeUtc: Now, CancellationToken.None);

        // No exception thrown and the store was called once — the executor skipped after the registry miss.
        await store.Received(1).GetByIdAsync(JobId, Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Happy path: enabled, known template → handler invoked once with correct context
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenValidDefinition_InvokesHandlerOnceWithCorrectContext()
    {
        const string paramJson = """{"key":"value"}""";
        const string fireInstanceId = "fire-happy-1";
        var scheduled = Now.AddMinutes(-1);

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: paramJson));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, fireInstanceId, scheduledFireTimeUtc: scheduled, actualFireTimeUtc: Now, CancellationToken.None);

        AssertEx.Equal(1, handler.InvocationCount, "Handler must be invoked exactly once.");

        var ctx = AssertEx.NotNull(handler.LastContext);
        AssertEx.Equal(JobId, ctx.ScheduledJobId, "ScheduledJobId must match the definition id.");
        AssertEx.Equal(TestEchoScheduledJobHandler.Id, ctx.TemplateId, "TemplateId must match.");
        AssertEx.Equal(paramJson, ctx.Parameters, "Parameters must carry the (decrypted) ParameterJson from the record.");
        AssertEx.Equal(fireInstanceId, ctx.FireInstanceId, "FireInstanceId must be threaded through.");
        AssertEx.True(ctx.ScheduledFireTimeUtc.HasValue, "ScheduledFireTimeUtc must not be null.");
        AssertEx.Equal(scheduled, ctx.ScheduledFireTimeUtc!.Value, "ScheduledFireTimeUtc must match.");
        AssertEx.Equal(Now, ctx.ActualFireTimeUtc, "ActualFireTimeUtc must match.");
    }

    [Test]
    public async Task DispatchAsync_WhenValidDefinitionWithNullParameters_InvokesHandlerWithNullParameters()
    {
        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: null));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, "fire-null-params", scheduledFireTimeUtc: null, actualFireTimeUtc: Now, CancellationToken.None);

        AssertEx.Equal(1, handler.InvocationCount, "Handler must be invoked exactly once.");
        AssertEx.Null(handler.LastContext?.Parameters, "Null parameters must propagate.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Cancellation propagates from handler
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DispatchAsync_WhenCancellationRequested_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        // The handler calls ThrowIfCancellationRequested — the exception must propagate.
        await AssertEx.ThrowsAsync<OperationCanceledException>(
            () => executor.DispatchAsync(JobId, "fire-cancel", scheduledFireTimeUtc: null, actualFireTimeUtc: Now, cts.Token));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static SchedulerDispatchExecutor CreateExecutor(
        IScheduledJobDefinitionStore store,
        IScheduledJobTemplateRegistry registry,
        IScheduledJobRunStore? runStore = null,
        IScheduledJobRunEventStore? eventStore = null)
    {
        runStore ??= CreateRunStoreSubstitute();
        eventStore ??= Substitute.For<IScheduledJobRunEventStore>();

        return new SchedulerDispatchExecutor(
            store,
            registry,
            runStore,
            eventStore,
            Substitute.For<ISchedulerEventPublisher>(),
            TimeProvider.System,
            NullLogger<SchedulerDispatchExecutor>.Instance);
    }

    // A substitute run store whose idempotent upsert echoes a fresh Running record, so the executor's post-guard
    // history recording proceeds to handler invocation. Lifecycle/get calls return defaults (ignored by the executor).
    private static IScheduledJobRunStore CreateRunStoreSubstitute()
    {
        var runStore = Substitute.For<IScheduledJobRunStore>();
        runStore.UpsertByFireInstanceAsync(Arg.Any<ScheduledJobRunInput>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(ToRunningRecord((ScheduledJobRunInput)callInfo[0])));
        return runStore;
    }

    private static ScheduledJobRunRecord ToRunningRecord(ScheduledJobRunInput input) =>
        new(
            Id: Guid.NewGuid(),
            ScheduledJobId: input.ScheduledJobId,
            TemplateId: input.TemplateId,
            QuartzFireInstanceId: input.QuartzFireInstanceId,
            TriggeredBy: input.TriggeredBy,
            Status: ScheduledRunStatus.Running,
            ScheduledFireTimeUtc: input.ScheduledFireTimeUtc,
            ActualFireTimeUtc: input.ActualFireTimeUtc,
            CompletedAtUtc: null,
            DurationMs: null,
            Summary: input.Summary,
            DetailsJson: input.DetailsJson,
            ErrorMessage: input.ErrorMessage,
            ErrorDetails: input.ErrorDetails,
            CancellationRequestedAtUtc: null,
            CreatedAtUtc: 1L);

    private static ScheduledJobDefinitionRecord BuildRecord(
        bool enabled,
        bool deleted,
        string templateId = TestEchoScheduledJobHandler.Id,
        string? parameterJson = null) =>
        new(
            Id: JobId,
            TemplateId: templateId,
            DisplayName: "Test Job",
            Description: null,
            Enabled: enabled,
            ScheduleKind: ScheduleKind.OneShot,
            CronExpression: null,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            TimeZoneId: "UTC",
            MisfirePolicy: SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            ParameterJson: parameterJson,
            CreatedBy: ScheduledJobCreator.User,
            CreatedAtUtc: 0L,
            UpdatedAtUtc: 0L,
            DisabledAtUtc: null,
            DeletedAtUtc: deleted ? 1L : null);
}
