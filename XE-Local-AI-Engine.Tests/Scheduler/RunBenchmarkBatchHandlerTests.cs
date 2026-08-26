namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="RunBenchmarkBatchHandler" /> tests: the descriptor refuses agent-created schedules and skips missed
///     fires, a malformed parameter payload is rejected WITHOUT freezing anything, a valid fire expands the matrix and
///     enqueues one freeze per cell (chaining the project version, never awaiting the runs), a project that still has
///     queued or running work is a SKIPPED fire rather than a second matrix, one refused cell does not cost the others,
///     a matrix where every cell failed surfaces as a sanitized <see cref="ScheduledJobExecutionException" /> on the job
///     run, and the per-fire time budget reports the untried cells instead of holding the fire.
/// </summary>
public sealed class RunBenchmarkBatchHandlerTests
{
    private const string ProjectIdString = "22222222-2222-2222-2222-222222222222";

    private static readonly Guid ProjectId = Guid.Parse(ProjectIdString);

    [Test]
    public void Descriptor_DisallowsAgentCreationAndSkipsMissedFires()
    {
        var harness = new Harness();

        AssertEx.Equal("run-benchmark-batch", harness.Handler.TemplateId);
        AssertEx.Equal("run-benchmark-batch", harness.Handler.Descriptor.TemplateId);
        // An AI agent may schedule a saved-agent run; it may not schedule GPU-hours.
        AssertEx.False(harness.Handler.Descriptor.AllowAgentCreation, "an AI agent must not be able to schedule a benchmark matrix.");
        AssertEx.True(harness.Handler.Descriptor.AllowManualTrigger, "an operator may fire a matrix on demand.");
        // A matrix missed while the node was off must not fire the moment it comes back.
        AssertEx.Equal(SchedulerMisfirePolicy.SkipMissed, harness.Handler.Descriptor.DefaultMisfirePolicy);
        AssertEx.Equal(ScheduleKind.Cron, harness.Handler.Descriptor.DefaultScheduleKind);
        // The fire only enqueues, so it carries no template ceiling of its own.
        AssertEx.Null(harness.Handler.Descriptor.DefaultMaxRuntimeSeconds);
        AssertEx.Equal(HistoryDetailLevel.Detailed, harness.Handler.Descriptor.HistoryDetailLevel);
        AssertEx.NotNull(harness.Handler.Descriptor.ParameterSchema);
        AssertEx.NotNull(harness.Handler.Descriptor.DefaultParameters);
    }

    [Test]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("""{ "models": ["a"] }""")]
    [Arguments("""{ "projectId": "not-a-guid", "models": ["a"] }""")]
    [Arguments("""{ "projectId": "00000000-0000-0000-0000-000000000000", "models": ["a"] }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222" }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": [] }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": ["  "] }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": ["a"], "kvCacheTypes": [] }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": ["a"], "kvCacheTypes": ["q3_k"] }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": ["a"], "repeatCount": 0 }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": ["a"], "repeatCount": 11 }""")]
    [Arguments("""{ "projectId": "22222222-2222-2222-2222-222222222222", "models": ["a","b","c","d","e","f","g","h","i","j","k"] }""")]
    public async Task ExecuteAsync_WhenParametersInvalid_ThrowsValidationExceptionWithoutFreezing(string parametersJson)
    {
        var harness = new Harness();

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => harness.Handler.ExecuteAsync(harness.Fire(parametersJson), CancellationToken.None));

        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task ExecuteAsync_WhenMatrixIsValid_EnqueuesEveryCellAndChainsTheProjectVersion()
    {
        var harness = new Harness();

        await harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None);

        // 2 models × 2 KV types = 4 cells, in model-major order.
        AssertEx.Equal(expected: 4, harness.FreezeCalls.Count);
        AssertEx.Equal("model-a", harness.FreezeCalls[0].PrimaryModelName);
        AssertEx.Equal("f16", harness.FreezeCalls[0].KvCacheType!);
        AssertEx.Equal("model-a", harness.FreezeCalls[1].PrimaryModelName);
        AssertEx.Equal("q8_0", harness.FreezeCalls[1].KvCacheType!);
        AssertEx.Equal("model-b", harness.FreezeCalls[2].PrimaryModelName);
        AssertEx.Equal("model-b", harness.FreezeCalls[3].PrimaryModelName);

        // Every group insert is all-or-nothing, so the version the next cell presents is the running total of runs
        // created so far — two runs per cell here (repeatCount 2).
        AssertEx.Equal(expected: 5L, harness.FreezeCalls[0].ExpectedProjectVersion);
        AssertEx.Equal(expected: 7L, harness.FreezeCalls[1].ExpectedProjectVersion);
        AssertEx.Equal(expected: 9L, harness.FreezeCalls[2].ExpectedProjectVersion);
        AssertEx.Equal(expected: 11L, harness.FreezeCalls[3].ExpectedProjectVersion);

        // The repeat/warm-up request rides through unchanged.
        AssertEx.Equal(expected: 2, harness.FreezeCalls[0].RepeatCount);
        AssertEx.True(harness.FreezeCalls[0].Warmup, "the matrix asked for a warm-up run.");

        // It enqueues and returns: nothing in the fire waits on a run reaching a terminal state.
        AssertEx.Contains(harness.Progress.Single(), "4/4 cell(s) enqueued", StringComparison.Ordinal);
        AssertEx.Contains(harness.Progress.Single(), "8 run(s) created", StringComparison.Ordinal);
    }

    [Test]
    public async Task ExecuteAsync_WhenNoKvCacheTypesGiven_EnqueuesOneAutoCellPerModel()
    {
        var harness = new Harness();

        await harness.Handler.ExecuteAsync(harness.Fire($$"""{ "projectId": "{{ProjectIdString}}", "models": ["model-a", "model-b"] }"""),
            CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.FreezeCalls.Count);
        // Absent means Auto, exactly as a null KV type does everywhere else in the module.
        AssertEx.Null(harness.FreezeCalls[0].KvCacheType);
        AssertEx.Null(harness.FreezeCalls[1].KvCacheType);
    }

    [Test]
    public async Task ExecuteAsync_WhenProjectStillHasQueuedWork_SkipsTheFireWithoutFreezing()
    {
        var harness = new Harness();
        harness.SetExistingRuns(BenchmarkPrimaryStatus.Succeeded, BenchmarkPrimaryStatus.Queued);

        await harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None);

        // A nightly matrix that fires while the previous night's is still draining must not queue a second matrix.
        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
        AssertEx.Contains(harness.Progress.Single(), "Skipped", StringComparison.Ordinal);
        AssertEx.Contains(harness.Progress.Single(), "1 run(s) queued or running", StringComparison.Ordinal);
    }

    [Test]
    public async Task ExecuteAsync_WhenProjectHasOnlyTerminalRuns_StillEnqueues()
    {
        var harness = new Harness();
        harness.SetExistingRuns(BenchmarkPrimaryStatus.Succeeded, BenchmarkPrimaryStatus.Failed, BenchmarkPrimaryStatus.Cancelled);

        await harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None);

        AssertEx.Equal(expected: 4, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task ExecuteAsync_WhenProjectIsMissing_FailsWithSanitizedReason()
    {
        var harness = new Harness();
        harness.Store.GetProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((BenchmarkProjectRecord?)null);

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() =>
            harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None));

        AssertEx.Contains(exception.Message, "could not be found", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task ExecuteAsync_WhenOneCellIsRefused_ContinuesAndReportsIt()
    {
        var harness = new Harness();
        harness.FailModel("model-a", new KeyNotFoundException("The installed model was not found."));

        await harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None);

        // One uninstalled model must not cost the operator the other cells.
        AssertEx.Equal(expected: 4, harness.FreezeCalls.Count);
        AssertEx.Equal(expected: 2, harness.StartedCells.Count);
        var summary = harness.Progress.Single();
        AssertEx.Contains(summary, "2/4 cell(s) enqueued", StringComparison.Ordinal);
        AssertEx.Contains(summary, "model-a (f16): the model is not installed", StringComparison.Ordinal);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheProjectVersionMoved_StopsTheMatrixAndNamesTheUntriedCells()
    {
        var harness = new Harness();
        harness.FailModel("model-b", new BenchmarkConflictException("VersionConflict"));

        await harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None);

        // A moved project version is a fact about the MATRIX: every remaining cell would fail identically, so the fire
        // stops rather than reporting the same reason four times.
        AssertEx.Equal(expected: 3, harness.FreezeCalls.Count);
        var summary = harness.Progress.Single();
        AssertEx.Contains(summary, "model-b (f16): VersionConflict", StringComparison.Ordinal);
        AssertEx.Contains(summary, "1 cell(s) not attempted after that", StringComparison.Ordinal);
    }

    [Test]
    public async Task ExecuteAsync_WhenEveryCellFailed_SurfacesTheFailureOnTheJobRun()
    {
        var harness = new Harness();
        harness.FailModel("model-a", new BenchmarkEligibilityException("The selected primary model is not an eligible local text-generation GGUF."));
        harness.FailModel("model-b", new BenchmarkEligibilityException("The selected primary model is not an eligible local text-generation GGUF."));

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() =>
            harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None));

        AssertEx.Contains(exception.Message, "No cell of the scheduled benchmark matrix could be enqueued", StringComparison.Ordinal);
        // The summary is still recorded before the failure, so the operator sees which cells were refused and why.
        AssertEx.Contains(harness.Progress.Single(), "0/4 cell(s) enqueued", StringComparison.Ordinal);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheFireTimeBudgetIsSpent_ReportsTheUntriedCells()
    {
        // The freeze is synchronous per cell, so a cold matrix must stop and say what it started rather than run into
        // Quartz's max-runtime interrupt with nothing recorded. The budget is per cell — 4 cells buy 180 s — so a
        // 60 s-per-check clock spends it on the fourth cell.
        var harness = new Harness(new BudgetBurningTimeProvider(TimeSpan.FromSeconds(60)));

        await harness.Handler.ExecuteAsync(harness.Fire(TwoByTwoMatrix()), CancellationToken.None);

        AssertEx.Equal(expected: 3, harness.FreezeCalls.Count);
        AssertEx.Contains(harness.Progress.Single(), "1 cell(s) not attempted", StringComparison.Ordinal);
        AssertEx.Contains(harness.Progress.Single(), "180 second budget", StringComparison.Ordinal);
    }

    private static string TwoByTwoMatrix() =>
        $$"""
          {
            "projectId": "{{ProjectIdString}}",
            "models": ["model-a", "model-b"],
            "kvCacheTypes": ["f16", "q8_0"],
            "repeatCount": 2,
            "warmup": true
          }
          """;

    /// <summary>A <see cref="TimeProvider" /> whose clock advances one fixed step per read, so a fire's budget can be
    ///     spent deterministically without sleeping.</summary>
    private sealed class BudgetBurningTimeProvider(TimeSpan step) : TimeProvider
    {
        private long _reads;

        public override long GetTimestamp()
        {
            // The first read is the fire's start stamp, so elapsed time only starts accruing from the second read on:
            // one step per budget check, which spends the 4-cell fire's budget on its fourth check.
            var elapsed = step * Math.Max(0, _reads++ - 1);
            return (long)(elapsed.TotalSeconds * TimestampFrequency);
        }
    }

    private sealed class Harness
    {
        private readonly Dictionary<string, Exception> _modelFailures = new(StringComparer.Ordinal);

        public Harness(TimeProvider? timeProvider = null)
        {
            Store.GetProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(new BenchmarkProjectRecord(ProjectId,
                     "Quant matrix",
                     ReadOnlyMemory<byte>.Empty,
                     ContextTokens: 4096,
                     AgentDefinitionId: Guid.NewGuid(),
                     JudgeEnabled: false,
                     CurrentJudgePolicyRevisionId: null,
                     IsFrozen: false,
                     Version: 5,
                     CreatedAtUtc: 0,
                     UpdatedAtUtc: 0));
            SetExistingRuns();

            Freeze.StartAsync(Arg.Any<BenchmarkRunStartRequest>(), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      var request = callInfo.Arg<BenchmarkRunStartRequest>();
                      FreezeCalls.Add(request);
                      if (_modelFailures.TryGetValue(request.PrimaryModelName, out var failure))
                      {
                          throw failure;
                      }

                      StartedCells.Add(request);
                      return Task.FromResult<IReadOnlyList<BenchmarkRunRecord>>(
                          [.. Enumerable.Range(0, request.RepeatCount).Select(static _ => Run(BenchmarkPrimaryStatus.Queued))]);
                  });

            var services = new ServiceCollection();
            services.AddSingleton(Store);
            services.AddSingleton(Freeze);
            var provider = services.BuildServiceProvider();

            Handler = new RunBenchmarkBatchHandler(provider.GetRequiredService<IServiceScopeFactory>(),
                timeProvider ?? TimeProvider.System,
                NullLogger<RunBenchmarkBatchHandler>.Instance);
        }

        public IBenchmarkStore Store { get; } = Substitute.For<IBenchmarkStore>();

        public IBenchmarkRunFreezeService Freeze { get; } = Substitute.For<IBenchmarkRunFreezeService>();

        public RunBenchmarkBatchHandler Handler { get; }

        public List<BenchmarkRunStartRequest> FreezeCalls { get; } = [];

        public List<BenchmarkRunStartRequest> StartedCells { get; } = [];

        public List<string> Progress { get; } = [];

        public void SetExistingRuns(params BenchmarkPrimaryStatus[] statuses)
        {
            Store.ListAllRunsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                 .Returns(new BenchmarkRunPage([.. statuses.Select(Run)], statuses.Length));
        }

        public void FailModel(string modelName, Exception exception) =>
            _modelFailures[modelName] = exception;

        private static ScheduledJobExecutionContext Context(string? parametersJson, Func<string, int?, CancellationToken, Task> reportProgress)
        {
            return new ScheduledJobExecutionContext
            {
                ScheduledJobId = Guid.NewGuid(),
                TemplateId = RunBenchmarkBatchHandler.TemplateIdValue,
                DisplayName = "Nightly quant matrix",
                Parameters = parametersJson,
                FireInstanceId = "fire-1",
                ScheduledFireTimeUtc = null,
                ActualFireTimeUtc = DateTimeOffset.UnixEpoch,
                TriggeredBy = ScheduledRunTrigger.Schedule,
                ReportProgressAsync = reportProgress
            };
        }

        private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus status) =>
            new(Guid.NewGuid(),
                ProjectId,
                ReadOnlyMemory<byte>.Empty,
                "model-a",
                null,
                "fingerprint",
                "agent",
                1,
                4096,
                status,
                null,
                null,
                null,
                null,
                null,
                0,
                null,
                null,
                1,
                0,
                null,
                null,
                0);

        public ScheduledJobExecutionContext Fire(string? parametersJson) =>
            Context(parametersJson, (summary, _, _) =>
            {
                Progress.Add(summary);
                return Task.CompletedTask;
            });
    }
}
