namespace XE_Local_AI_Engine.Tests.Training.Evaluation;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ComparisonBenchmarkHandoffService" /> tests: the hand-off creates the benchmark project for a training
///     comparison and enqueues the paired base/tuned runs against it (same task, same KV type, same repeat count, one
///     shared freeze scope, both sides frozen against the same project version and committed together); it reuses a project already named for the
///     comparison instead of scattering near-duplicates; the benchmark task is REQUIRED from the operator; a tuned side
///     that is still a staged artifact — not yet an installed model with a <c>Trained</c> origin — is refused with that
///     reason rather than failing inside the freeze; and the freeze's bare <see cref="KeyNotFoundException" /> for an
///     uninstalled model is translated into a named, operator-actionable refusal instead of escaping as a 500.
/// </summary>
public sealed class ComparisonBenchmarkHandoffServiceTests
{
    private const string BaseModelName = "qwen3.8-27b-Q4_K_M";
    private const string TunedModelName = "qwen3.8-27b-tuned-Q4_K_M";
    private const string CoreTask = "Summarize the release notes in five bullets.";

    private static readonly Guid ComparisonId = Guid.NewGuid();
    private static readonly Guid BaseEvaluationId = Guid.NewGuid();
    private static readonly Guid TunedEvaluationId = Guid.NewGuid();
    private static readonly Guid ArtifactId = Guid.NewGuid();
    private static readonly Guid AgentDefinitionId = Guid.NewGuid();

    [Test]
    public async Task CreateAsync_CreatesProjectAndPairedRuns()
    {
        var harness = new Harness();

        var result = await harness.Service.CreateAsync(Command(), CancellationToken.None);

        AssertEx.Equal(harness.CreatedProjectId, result.ProjectId);
        AssertEx.Equal(BaseModelName, result.BaseModelName);
        // The tuned side is a staged artifact evaluation, so its INSTALLED name comes from the promoted registry entry.
        AssertEx.Equal(TunedModelName, result.TunedModelName);

        AssertEx.Equal(expected: 2, harness.FreezeCalls.Count);
        AssertEx.Equal(BaseModelName, harness.FreezeCalls[0].PrimaryModelName);
        AssertEx.Equal(TunedModelName, harness.FreezeCalls[1].PrimaryModelName);
        // Paired by construction: same project, same KV type, same repeat count. Only the model differs.
        AssertEx.Equal(harness.CreatedProjectId, harness.FreezeCalls[0].ProjectId);
        AssertEx.Equal(harness.CreatedProjectId, harness.FreezeCalls[1].ProjectId);
        AssertEx.Equal("q8_0", harness.FreezeCalls[0].KvCacheType!);
        AssertEx.Equal("q8_0", harness.FreezeCalls[1].KvCacheType!);
        AssertEx.Equal(harness.FreezeCalls[0].RepeatCount, harness.FreezeCalls[1].RepeatCount);
        // Nothing is committed between the two freezes, so both sides present the SAME project version.
        AssertEx.Equal(expected: 0L, harness.FreezeCalls[0].ExpectedProjectVersion);
        AssertEx.Equal(expected: 0L, harness.FreezeCalls[1].ExpectedProjectVersion);
        // One scope for the pair: the tuned side cannot be frozen against different bytes than the base side was.
        AssertEx.Equal(expected: 1, harness.FreezeScopes.Distinct().Count());

        // ONE insert carrying both groups, and the response names each side's ids rather than one flat list.
        AssertEx.Equal(expected: 1, harness.CommittedBatches.Count);
        AssertEx.Equal(expected: 2, harness.CommittedBatches[0].Count);
        AssertEx.Equal(expected: 2, result.BaseRunIds.Count);
        AssertEx.Equal(expected: 2, result.TunedRunIds.Count);
        AssertEx.Equal(expected: 0, result.BaseRunIds.Intersect(result.TunedRunIds).Count());
        // The project carries the operator's task, not the comparison's evaluation prompt.
        AssertEx.Equal(CoreTask, harness.CreatedDraft!.CoreTask);
        AssertEx.Equal("Tuned vs base", harness.CreatedDraft.Name);
    }

    [Test]
    public async Task CreateAsync_WhenAProjectIsAlreadyNamedForTheComparison_ReusesIt()
    {
        var harness = new Harness();
        var existing = Project(Guid.NewGuid(), "Tuned vs base", version: 7);
        harness.Benchmarks.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<BenchmarkProjectRecord>>([existing]);

        var result = await harness.Service.CreateAsync(Command(), CancellationToken.None);

        // Re-running the hand-off after a failed pair must add runs to the same cohort, not to a near-identical project.
        AssertEx.Equal(existing.Id, result.ProjectId);
        AssertEx.Null(harness.CreatedDraft);
        AssertEx.Equal(expected: 7L, harness.FreezeCalls[0].ExpectedProjectVersion);
        AssertEx.Equal(expected: 7L, harness.FreezeCalls[1].ExpectedProjectVersion);
    }

    [Test]
    public async Task CreateAsync_WhenNoNameGiven_UsesTheComparisonsOwnName()
    {
        var harness = new Harness();

        await harness.Service.CreateAsync(Command() with
        {
            Name = null
        }, CancellationToken.None);

        AssertEx.Equal("Nightly tune", harness.CreatedDraft!.Name);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task CreateAsync_WithoutCoreTask_IsRejected(string coreTask)
    {
        var harness = new Harness();

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.Service.CreateAsync(Command() with
        {
            CoreTask = coreTask
        }, CancellationToken.None));

        // The evaluation prompt is a scoring-harness input, not a benchmark task; reusing it would measure the wrong thing.
        AssertEx.Contains(exception.Message, "A benchmark task is required", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task CreateAsync_WhenKvCacheTypeIsUnsupported_IsRejected()
    {
        var harness = new Harness();

        await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.Service.CreateAsync(Command() with
        {
            KvCacheType = "q3_k"
        }, CancellationToken.None));

        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task CreateAsync_WhenComparisonIsMissing_IsRejected()
    {
        var harness = new Harness();
        harness.Evaluations.GetComparisonAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TrainingComparisonRecord?)null);

        await AssertEx.ThrowsAsync<BenchmarkNotFoundException>(() => harness.Service.CreateAsync(Command(), CancellationToken.None));
    }

    [Test]
    public async Task CreateAsync_WhenTunedArtifactIsNotRegisteredYet_IsRejectedWithThatReason()
    {
        var harness = new Harness();
        harness.Runs.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(committedModelName: null));

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.Service.CreateAsync(Command(), CancellationToken.None));

        // A staged GGUF is not launchable by the benchmark harness — it becomes one only when promotion registers it as
        // an installed model (origin `trained`), which is also what gives it the name used here.
        AssertEx.Contains(exception.Message, "still a staged artifact", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task CreateAsync_WhenBothSidesResolveToOneInstalledModel_IsRejected()
    {
        var harness = new Harness();
        harness.Runs.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(BaseModelName));

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.Service.CreateAsync(Command(), CancellationToken.None));

        // Two runs of the same model are not a comparison; refuse before enqueuing an hour of GPU time.
        AssertEx.Contains(exception.Message, "nothing to compare", StringComparison.Ordinal);
        AssertEx.Equal(expected: 0, harness.FreezeCalls.Count);
    }

    [Test]
    public async Task CreateAsync_WhenAModelIsNotInstalled_NamesItInsteadOfEscapingAsAKeyNotFound()
    {
        var harness = new Harness();
        harness.FreezeFailure = new KeyNotFoundException("installed model");

        var exception = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.Service.CreateAsync(Command(), CancellationToken.None));

        // A bare KeyNotFoundException is in no benchmark exception family, so it would have escaped as a 500.
        AssertEx.Contains(exception.Message, BaseModelName, StringComparison.Ordinal);
        AssertEx.Contains(exception.Message, "is not installed on this node", StringComparison.Ordinal);
    }

    [Test]
    public async Task CreateAsync_WhenTheTunedSideCannotBeFrozen_CommitsNothing()
    {
        var harness = new Harness();
        harness.FailingModelName = TunedModelName;
        harness.FreezeFailure = new BenchmarkEligibilityException("The selected model could not be verified against its installed registry entry.");

        await AssertEx.ThrowsAsync<BenchmarkEligibilityException>(() => harness.Service.CreateAsync(Command(), CancellationToken.None));

        // The base side is frozen by the time the tuned side is refused. Committing it anyway queued an hour of GPU
        // time the caller was never told the ids of, and the only retry available then queued a SECOND base group.
        AssertEx.Equal(expected: 2, harness.FreezeCalls.Count);
        AssertEx.Equal(expected: 0, harness.CommittedBatches.Count);
    }

    /// <summary>
    ///     A name is not an identity. Reusing a same-named project that holds a DIFFERENT task benchmarked both models
    ///     against a question the operator never asked, with nothing saying so.
    /// </summary>
    [Test]
    [Arguments("Answer the support ticket.", 8192)]
    [Arguments(CoreTask, 32768)]
    public async Task CreateAsync_WhenTheSameNameHoldsADifferentBenchmark_CreatesADisambiguatedProject(string coreTask, int contextTokens)
    {
        var harness = new Harness();
        var unrelated = Project(Guid.NewGuid(), "Tuned vs base", version: 7, coreTask, contextTokens);
        harness.Benchmarks.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<BenchmarkProjectRecord>>([unrelated]);

        var result = await harness.Service.CreateAsync(Command(), CancellationToken.None);

        AssertEx.Equal(harness.CreatedProjectId, result.ProjectId);
        // Suffixed rather than merged: two comparisons under one name are two cohorts.
        AssertEx.Equal("Tuned vs base (2)", harness.CreatedDraft!.Name);
        AssertEx.Equal(CoreTask, harness.CreatedDraft.CoreTask);
    }

    [Test]
    public async Task CreateAsync_WhenTheSameNameHoldsADifferentAgent_CreatesANewProject()
    {
        var harness = new Harness();
        var unrelated = Project(Guid.NewGuid(), "Tuned vs base", version: 7) with
        {
            AgentDefinitionId = Guid.NewGuid()
        };
        harness.Benchmarks.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<BenchmarkProjectRecord>>([unrelated]);

        var result = await harness.Service.CreateAsync(Command(), CancellationToken.None);

        AssertEx.Equal(harness.CreatedProjectId, result.ProjectId);
        AssertEx.Equal("Tuned vs base (2)", harness.CreatedDraft!.Name);
    }

    private static CreateBenchmarkFromComparisonCommand Command() =>
        new(ComparisonId, CoreTask, ContextTokens: 8192, AgentDefinitionId, "Tuned vs base", "q8_0", RepeatCount: 2);

    private static BenchmarkProjectRecord Project(Guid id, string name, long version, string coreTask = CoreTask, int contextTokens = 8192) =>
        new(id,
            name,
            JsonSerializer.SerializeToUtf8Bytes(coreTask),
            contextTokens,
            AgentDefinitionId,
            JudgeEnabled: false,
            CurrentJudgePolicyRevisionId: null,
            IsFrozen: false,
            version,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);

    private static TrainingArtifactRecord Artifact(string? committedModelName) =>
        new(ArtifactId,
            Guid.NewGuid(),
            TrainingArtifactKind.MergedGguf,
            "/models/tuned.gguf",
            "sha",
            SizeBytes: 1,
            TrainingArtifactSmokeState.Passed,
            SmokeReason: null,
            committedModelName,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);

    private sealed class Harness
    {
        public Harness()
        {
            Evaluations.GetComparisonAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                       .Returns(new TrainingComparisonRecord(ComparisonId,
                           "Nightly tune",
                           BaseEvaluationId,
                           TunedEvaluationId,
                           BaseBenchmarkRunId: null,
                           TunedBenchmarkRunId: null,
                           TrainingRunId: Guid.NewGuid(),
                           DeltasJson: ReadOnlyMemory<byte>.Empty,
                           Version: 1,
                           CreatedAtUtc: 0,
                           UpdatedAtUtc: 0));
            Evaluations.GetAsync(BaseEvaluationId, Arg.Any<CancellationToken>())
                       .Returns(EvaluationRecord(BaseEvaluationId, BaseModelName, EvaluationModelTargetKind.InstalledModel, sourceArtifactId: null));
            Evaluations.GetAsync(TunedEvaluationId, Arg.Any<CancellationToken>())
                       .Returns(EvaluationRecord(TunedEvaluationId, "tuned.gguf", EvaluationModelTargetKind.StagedTrainingArtifact, ArtifactId));
            Runs.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(TunedModelName));
            Benchmarks.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<BenchmarkProjectRecord>>([]);
            Projects.CreateAsync(Arg.Any<BenchmarkProjectDraft>(), Arg.Any<CancellationToken>())
                    .Returns(callInfo =>
                    {
                        CreatedDraft = callInfo.Arg<BenchmarkProjectDraft>();
                        return Task.FromResult(Project(CreatedProjectId, CreatedDraft.Name, version: 0));
                    });
            Freeze.FreezeAsync(Arg.Any<BenchmarkRunStartRequest>(), Arg.Any<BenchmarkFreezeScope?>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      var request = callInfo.Arg<BenchmarkRunStartRequest>();
                      FreezeCalls.Add(request);
                      FreezeScopes.Add(callInfo.ArgAt<BenchmarkFreezeScope?>(1));
                      if (FreezeFailure is { } failure && request.PrimaryModelName == FailingModelName)
                      {
                          throw failure;
                      }

                      return Task.FromResult(new BenchmarkFrozenRunPlan(request.ProjectId,
                          request.ExpectedProjectVersion,
                          [.. Enumerable.Range(0, request.RepeatCount).Select(_ => FrozenCommand(request))]));
                  });
            Freeze.CommitAsync(Arg.Any<IReadOnlyList<BenchmarkFrozenRunPlan>>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo =>
                  {
                      var plans = callInfo.Arg<IReadOnlyList<BenchmarkFrozenRunPlan>>();
                      CommittedBatches.Add(plans);
                      return Task.FromResult<IReadOnlyList<IReadOnlyList<BenchmarkRunRecord>>>([
                          .. plans.Select(static plan => (IReadOnlyList<BenchmarkRunRecord>)[.. plan.Commands.Select(static _ => Run())])
                      ]);
                  });

            Service = new ComparisonBenchmarkHandoffService(Evaluations, Runs, Benchmarks, Projects, Freeze);
        }

        public ITrainingEvaluationStore Evaluations { get; } = Substitute.For<ITrainingEvaluationStore>();

        public ITrainingRunStore Runs { get; } = Substitute.For<ITrainingRunStore>();

        public IBenchmarkStore Benchmarks { get; } = Substitute.For<IBenchmarkStore>();

        public IBenchmarkProjectService Projects { get; } = Substitute.For<IBenchmarkProjectService>();

        public IBenchmarkRunFreezeService Freeze { get; } = Substitute.For<IBenchmarkRunFreezeService>();

        public ComparisonBenchmarkHandoffService Service { get; }

        public Guid CreatedProjectId { get; } = Guid.NewGuid();

        public BenchmarkProjectDraft? CreatedDraft { get; private set; }

        public List<BenchmarkRunStartRequest> FreezeCalls { get; } = [];

        public List<BenchmarkFreezeScope?> FreezeScopes { get; } = [];

        public List<IReadOnlyList<BenchmarkFrozenRunPlan>> CommittedBatches { get; } = [];

        public Exception? FreezeFailure { get; set; }

        /// <summary>Which side <see cref="FreezeFailure" /> belongs to. The base model by default.</summary>
        public string FailingModelName { get; set; } = BaseModelName;

        private static TrainingEvaluationRecord EvaluationRecord(Guid id,
            string modelName,
            EvaluationModelTargetKind targetKind,
            Guid? sourceArtifactId) =>
            new(id,
                TrainingRunId: Guid.NewGuid(),
                ComparisonId: ComparisonId,
                modelName,
                ModelContentFingerprint: "v1:model",
                DatasetId: Guid.NewGuid(),
                DatasetContentFingerprint: "v1:dataset",
                MembershipJson: ReadOnlyMemory<byte>.Empty,
                TrainingEvaluationStatus.Succeeded,
                ResultsJson: null,
                TotalCount: 1,
                ScoredCount: 1,
                PassedCount: 1,
                PerKindJson: null,
                ErrorMessage: null,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                TrainingWorkStatus.Succeeded,
                targetKind,
                sourceArtifactId);

        /// <summary>A placeholder frozen command: only the COUNT is load-bearing in these tests.</summary>
        private static BenchmarkStartRunCommand FrozenCommand(BenchmarkRunStartRequest request) =>
            new(Guid.NewGuid(),
                request.ProjectId,
                request.ExpectedProjectVersion,
                ReadOnlyMemory<byte>.Empty,
                request.PrimaryModelName,
                null,
                "fingerprint",
                "agent",
                1,
                8192);

        private static BenchmarkRunRecord Run() =>
            new(Guid.NewGuid(),
                Guid.NewGuid(),
                ReadOnlyMemory<byte>.Empty,
                BaseModelName,
                null,
                "fingerprint",
                "agent",
                1,
                8192,
                BenchmarkPrimaryStatus.Queued,
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
    }
}
