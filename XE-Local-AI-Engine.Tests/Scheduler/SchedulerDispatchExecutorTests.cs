namespace XE_Local_AI_Engine.Tests.Scheduler;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for <see cref="SchedulerDispatchExecutor" />.
///     Guards (null def / disabled / soft-deleted / unknown template) are verified without touching the handler;
///     the happy path confirms the handler is invoked exactly once with the correct context.
/// </summary>
public sealed class SchedulerDispatchExecutorTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(year: 2026, month: 6, day: 1, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

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

        await executor.DispatchAsync(JobId, "fire-1", Now, Now, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.InvocationCount, "Handler must not be invoked when definition is missing.");
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

        await executor.DispatchAsync(JobId, "fire-2", Now, Now, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.InvocationCount, "Handler must not be invoked for a disabled definition.");
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

        await executor.DispatchAsync(JobId, "fire-3", Now, Now, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.InvocationCount, "Handler must not be invoked for a soft-deleted definition.");
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
             .Returns(BuildRecord(enabled: true, deleted: false, "unknown.template"));

        // Registry contains only test.echo — "unknown.template" will miss.
        var registry = new ScheduledJobTemplateRegistry([new TestEchoScheduledJobHandler()]);
        var executor = CreateExecutor(store, registry);

        await executor.DispatchAsync(JobId, "fire-4", Now, Now, CancellationToken.None);

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

        await executor.DispatchAsync(JobId, fireInstanceId, scheduled, Now, CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.InvocationCount, "Handler must be invoked exactly once.");

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

        await executor.DispatchAsync(JobId, "fire-null-params", scheduledFireTimeUtc: null, Now, CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.InvocationCount, "Handler must be invoked exactly once.");
        AssertEx.Null(handler.LastContext?.Parameters, "Null parameters must propagate.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Per-fire use-case override merge (manual model-fit refresh)
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Dispatch_WhenOverridePresent_MergesUseCaseIntoParameters()
    {
        // A baked model-fit parameter set; the manual refresh should swap ONLY its use-case.
        const string storedJson =
            """{"approvedImageId":"llmfit-recommender-0-9-30","operation":"Recommend","useCase":"coding","limit":5,"providerName":"ollama"}""";

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: storedJson));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchedulerJobKeys.ModelFitUseCaseOverrideKey] = "general"
        };

        await executor.DispatchAsync(JobId,
            "fire-override",
            Now,
            Now,
            CancellationToken.None,
            overrides);

        var parameters = AssertEx.NotNull(handler.LastContext?.Parameters);
        using var document = JsonDocument.Parse(parameters);
        var root = document.RootElement;

        AssertEx.Equal("general", root.GetProperty("useCase").GetString(), "Only the use-case must be overridden.");
        AssertEx.Equal("llmfit-recommender-0-9-30", root.GetProperty("approvedImageId").GetString(), "approvedImageId must be untouched.");
        AssertEx.Equal("Recommend", root.GetProperty("operation").GetString(), "operation must be untouched.");
        AssertEx.Equal(expected: 5, root.GetProperty("limit").GetInt32(), "limit must be untouched.");
        AssertEx.Equal("ollama", root.GetProperty("providerName").GetString(), "providerName must be untouched.");
    }

    [Test]
    public async Task Dispatch_WhenLimitOverridePresent_MergesLimitAsNumber()
    {
        const string storedJson =
            """{"approvedImageId":"llmfit-recommender-0-9-30","operation":"Recommend","useCase":"coding","limit":5,"providerName":"ollama"}""";

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: storedJson));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchedulerJobKeys.ModelFitLimitOverrideKey] = "20"
        };

        await executor.DispatchAsync(JobId, "fire-limit", Now, Now, CancellationToken.None, overrides);

        var parameters = AssertEx.NotNull(handler.LastContext?.Parameters);
        using var document = JsonDocument.Parse(parameters);
        var root = document.RootElement;

        AssertEx.Equal(JsonValueKind.Number, root.GetProperty("limit").ValueKind, "limit must be written back as a JSON number, not a string.");
        AssertEx.Equal(expected: 20, root.GetProperty("limit").GetInt32(), "Only the limit must be overridden.");
        AssertEx.Equal("coding", root.GetProperty("useCase").GetString(), "useCase must be untouched when only limit is overridden.");
        AssertEx.Equal("ollama", root.GetProperty("providerName").GetString(), "providerName must be untouched.");
    }

    [Test]
    public async Task Dispatch_WhenUseCaseAndLimitOverride_MergesBothWhitelistedKeysOnly()
    {
        const string storedJson =
            """{"approvedImageId":"llmfit-recommender-0-9-30","operation":"Recommend","useCase":"coding","limit":5,"providerName":"ollama"}""";

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: storedJson));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SchedulerJobKeys.ModelFitUseCaseOverrideKey] = "general",
            [SchedulerJobKeys.ModelFitLimitOverrideKey] = "15"
        };

        await executor.DispatchAsync(JobId, "fire-both", Now, Now, CancellationToken.None, overrides);

        var parameters = AssertEx.NotNull(handler.LastContext?.Parameters);
        using var document = JsonDocument.Parse(parameters);
        var root = document.RootElement;

        AssertEx.Equal("general", root.GetProperty("useCase").GetString(), "useCase must be overridden.");
        AssertEx.Equal(expected: 15, root.GetProperty("limit").GetInt32(), "limit must be overridden.");
        AssertEx.Equal("llmfit-recommender-0-9-30", root.GetProperty("approvedImageId").GetString(), "approvedImageId must be untouched.");
        AssertEx.Equal("ollama", root.GetProperty("providerName").GetString(), "providerName must be untouched.");
    }

    [Test]
    public async Task Dispatch_WhenOverrideCarriesNonWhitelistedKey_IgnoresItAndPassesStoredThrough()
    {
        const string storedJson =
            """{"approvedImageId":"llmfit-recommender-0-9-30","operation":"Recommend","useCase":"coding","limit":5,"providerName":"ollama"}""";

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: storedJson));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        // A per-fire map carrying only a NON-whitelisted key must not override any stored parameter — the stored JSON
        // passes through verbatim. This is the security boundary: only the two whitelisted keys can ever override.
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["providerName"] = "evil",
            ["operation"] = "Benchmark"
        };

        await executor.DispatchAsync(JobId, "fire-nonwhitelisted", Now, Now, CancellationToken.None, overrides);

        AssertEx.Equal(storedJson, handler.LastContext?.Parameters, "A non-whitelisted override key must leave the stored parameters unchanged.");
    }

    [Test]
    public async Task Dispatch_WhenNoOverride_UsesStoredParameters()
    {
        // The cron / no-override path must hand the handler the stored parameters verbatim (baked use-case unchanged).
        const string storedJson =
            """{"approvedImageId":"llmfit-recommender-0-9-30","operation":"Recommend","useCase":"coding","limit":5,"providerName":"ollama"}""";

        var handler = new TestEchoScheduledJobHandler();
        var store = Substitute.For<IScheduledJobDefinitionStore>();
        store.GetByIdAsync(JobId, Arg.Any<CancellationToken>())
             .Returns(BuildRecord(enabled: true, deleted: false, parameterJson: storedJson));

        var registry = new ScheduledJobTemplateRegistry([handler]);
        var executor = CreateExecutor(store, registry);

        // No parameterOverrides argument → the recurring/cron dispatch behavior.
        await executor.DispatchAsync(JobId, "fire-no-override", Now, Now, CancellationToken.None);

        AssertEx.Equal(storedJson, handler.LastContext?.Parameters, "Stored parameters must pass through unchanged with no override.");
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
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => executor.DispatchAsync(JobId, "fire-cancel", scheduledFireTimeUtc: null, Now, cts.Token));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private static SchedulerDispatchExecutor CreateExecutor(IScheduledJobDefinitionStore store,
        IScheduledJobTemplateRegistry registry,
        IScheduledJobRunStore? runStore = null,
        IScheduledJobRunEventStore? eventStore = null)
    {
        runStore ??= CreateRunStoreSubstitute();
        eventStore ??= Substitute.For<IScheduledJobRunEventStore>();

        return new SchedulerDispatchExecutor(store,
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

    private static ScheduledJobRunRecord ToRunningRecord(ScheduledJobRunInput input)
    {
        return new ScheduledJobRunRecord(Guid.NewGuid(),
            input.ScheduledJobId,
            input.TemplateId,
            input.QuartzFireInstanceId,
            input.TriggeredBy,
            ScheduledRunStatus.Running,
            input.ScheduledFireTimeUtc,
            input.ActualFireTimeUtc,
            CompletedAtUtc: null,
            DurationMs: null,
            input.Summary,
            input.DetailsJson,
            input.ErrorMessage,
            input.ErrorDetails,
            CancellationRequestedAtUtc: null,
            CreatedAtUtc: 1L);
    }

    private static ScheduledJobDefinitionRecord BuildRecord(bool enabled,
        bool deleted,
        string templateId = TestEchoScheduledJobHandler.Id,
        string? parameterJson = null)
    {
        return new ScheduledJobDefinitionRecord(JobId,
            templateId,
            "Test Job",
            Description: null,
            enabled,
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
            parameterJson,
            ScheduledJobCreator.User,
            CreatedAtUtc: 0L,
            UpdatedAtUtc: 0L,
            DisabledAtUtc: null,
            deleted ? 1L : null);
    }
}
