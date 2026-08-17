namespace XE_Local_AI_Engine.Tests.Training.Export;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Export;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ArtifactQualityServiceTests
{
    [Test]
    public async Task Decide_NoRegressionsAndExactEvidence_PersistsPass()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.Equal(ArtifactQualityOutcome.Passed, decision.Outcome);
        AssertEx.Equal(harness.Artifact.Sha256!, decision.ArtifactSha256);
    }

    [Test]
    [Arguments(-0.01, 0, "AggregateRegression")]
    [Arguments(0, -0.01, "PerKindRegression")]
    public async Task Decide_Regression_PersistsFailedDecision(double aggregateDelta, double kindDelta, string failureCode)
    {
        var harness = Harness.Create(aggregateDelta, kindDelta);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.Equal(ArtifactQualityOutcome.Failed, decision.Outcome);
        AssertEx.True(decision.FailureCodes.Contains(failureCode, StringComparer.Ordinal));
    }

    [Test]
    public async Task Override_CompleteFailedDecision_RequiresAndAuditsReason()
    {
        var harness = Harness.Create(aggregateDelta: -0.01, kindDelta: 0);
        var failed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var overridden = await harness.Service.OverrideAsync(failed.Id, failed.Version, "operator accepts known regression");

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(overridden));
        AssertEx.Equal(ArtifactQualityOutcome.Overridden, decision.Outcome);
        AssertEx.Equal("operator accepts known regression", decision.OverrideReason!);
        AssertEx.True(decision.OverriddenAtUtc.HasValue);
    }

    [Test]
    public async Task Decide_TunedArtifactIdentityDrift_FailsClosed()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, tunedFingerprint: "different");

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.True(decision.FailureCodes.Contains("TunedIdentityMismatch", StringComparer.Ordinal));
    }

    [Test]
    [Arguments(true, false, "ComparisonRunMismatch")]
    [Arguments(false, true, "EvaluationRunMismatch")]
    public async Task Decide_RunLineageMismatch_FailsClosed(bool comparisonRunMismatch, bool evaluationRunMismatch,
        string expectedFailure)
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, comparisonRunMismatch: comparisonRunMismatch,
            evaluationRunMismatch: evaluationRunMismatch);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.Equal(ArtifactQualityOutcome.Failed, decision.Outcome);
        AssertEx.True(decision.FailureCodes.Contains(expectedFailure, StringComparer.Ordinal));
    }

    [Test]
    public async Task Decide_BenchmarkRegressionIsAdvisoryWhenAccuracyDoesNotRegress()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, benchmarkRegression: true);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.Equal(ArtifactQualityOutcome.Passed, decision.Outcome);
        AssertEx.Empty(decision.FailureCodes);
    }

    [Test]
    public async Task Decide_WhenStoredWinIsStaleAfterFinalEvaluationRegression_FailsClosed()
    {
        var harness = Harness.Create(aggregateDelta: 1, kindDelta: 1, currentEvaluationRegressed: true);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.Equal(ArtifactQualityOutcome.Failed, decision.Outcome);
        AssertEx.True(decision.FailureCodes.Contains("ComparisonDeltasMismatch", StringComparer.Ordinal));
        AssertEx.True(decision.FailureCodes.Contains("AggregateRegression", StringComparer.Ordinal));
    }

    [Test]
    public async Task Decide_WhenStoredDeltasAreTamperedButCurrentEvaluationPasses_FailsClosed()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, storedDeltasTampered: true);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.Equal(ArtifactQualityOutcome.Failed, decision.Outcome);
        AssertEx.True(decision.FailureCodes.Contains("ComparisonDeltasMismatch", StringComparer.Ordinal));
        AssertEx.False(decision.FailureCodes.Contains("AggregateRegression", StringComparer.Ordinal));
    }

    [Test]
    public async Task BeginRevalidation_ReplacesPromotableDecisionWithPendingAndRetainsAudit()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);
        var passed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var pending = await harness.Service.BeginRevalidationAsync(passed.Id, passed.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(pending));
        AssertEx.Equal(ArtifactQualityOutcome.Pending, decision.Outcome);
        AssertEx.Equal(harness.Comparison.Id, decision.ComparisonId);
        AssertEx.Equal(expected: 1, decision.History.Count);
        AssertEx.Equal(ArtifactQualityOutcome.Passed, decision.History[0].Outcome);
        AssertEx.Equal(harness.Comparison.Id, decision.History[0].ComparisonId);
        AssertEx.Equal(harness.Artifact.Sha256!, decision.History[0].ArtifactSha256);
        AssertEx.Equal(ArtifactQualityDecisionV1.CurrentPolicyVersion, decision.History[0].PolicyVersion);
    }

    [Test]
    [Arguments(false, ArtifactQualityOutcome.Failed)]
    [Arguments(true, ArtifactQualityOutcome.Overridden)]
    public async Task BeginRevalidation_RetainsFailedAndOverriddenDecisionHistory(bool overrideFailure,
        ArtifactQualityOutcome expectedPriorOutcome)
    {
        var harness = Harness.Create(aggregateDelta: -0.01, kindDelta: 0);
        var decided = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);
        if (overrideFailure)
        {
            decided = await harness.Service.OverrideAsync(decided.Id, decided.Version, "operator accepts known regression");
        }

        var pending = await harness.Service.BeginRevalidationAsync(decided.Id, decided.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(pending));
        AssertEx.Equal(ArtifactQualityOutcome.Pending, decision.Outcome);
        AssertEx.Equal(expected: 1, decision.History.Count);
        AssertEx.Equal(expectedPriorOutcome, decision.History[0].Outcome);
        if (overrideFailure)
        {
            AssertEx.Equal("operator accepts known regression", decision.History[0].OverrideReason);
        }
        else
        {
            AssertEx.Null(decision.History[0].OverrideReason);
        }
    }

    [Test]
    public async Task Decide_AfterRevalidation_RejectsReusedComparisonAndEvaluationPair()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);
        var passed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);
        var pending = await harness.Service.BeginRevalidationAsync(passed.Id, passed.Version);

        var rejection = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.DecideAsync(pending.Id, harness.Comparison.Id, pending.Version));

        AssertEx.Contains(rejection.Message, ArtifactQualityService.RevalidationEvidenceReusedCode, StringComparison.Ordinal);
        AssertEx.Contains(rejection.Message, "fresh comparison and fresh base and tuned evaluations", StringComparison.Ordinal);
    }

    [Test]
    public async Task Decide_AfterRevalidation_AcceptsFreshComparisonPairAndCarriesPriorAudit()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);
        var passed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);
        var pending = await harness.Service.BeginRevalidationAsync(passed.Id, passed.Version);
        var fresh = harness.AddFreshComparisonPair();

        var redecided = await harness.Service.DecideAsync(pending.Id, fresh.Id, pending.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(redecided));
        AssertEx.Equal(ArtifactQualityOutcome.Passed, decision.Outcome);
        AssertEx.Equal(fresh.Id, decision.ComparisonId);
        AssertEx.Equal(expected: 1, decision.History.Count);
        AssertEx.Equal(ArtifactQualityOutcome.Passed, decision.History[0].Outcome);
    }

    [Test]
    public async Task Decide_AfterTwoRevalidationCycles_RejectsOldestTripletAndPreservesPendingAudit()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);
        var first = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);
        var firstPending = await harness.Service.BeginRevalidationAsync(first.Id, first.Version);
        var secondComparison = harness.AddComparisonPair();
        var second = await harness.Service.DecideAsync(firstPending.Id, secondComparison.Id, firstPending.Version);
        var secondPending = await harness.Service.BeginRevalidationAsync(second.Id, second.Version);

        var rejection = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.DecideAsync(secondPending.Id, harness.Comparison.Id, secondPending.Version));
        var unchanged = await harness.Service.BeginRevalidationAsync(secondPending.Id, secondPending.Version);

        AssertEx.Contains(rejection.Message, ArtifactQualityService.RevalidationEvidenceReusedCode, StringComparison.Ordinal);
        AssertEx.Equal(secondPending.Version, unchanged.Version);
        var pendingDecision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(unchanged));
        AssertEx.Equal(ArtifactQualityOutcome.Pending, pendingDecision.Outcome);
        AssertEx.Equal(expected: 2, pendingDecision.History.Count);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task Decide_AfterTwoRevalidationCycles_RejectsOlderMixedSideReuseThenAcceptsFreshPair(
        bool reuseOldestBase,
        bool reuseOldestTuned)
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);
        var first = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);
        var firstPending = await harness.Service.BeginRevalidationAsync(first.Id, first.Version);
        var secondComparison = harness.AddComparisonPair();
        var second = await harness.Service.DecideAsync(firstPending.Id, secondComparison.Id, firstPending.Version);
        var secondPending = await harness.Service.BeginRevalidationAsync(second.Id, second.Version);
        var mixed = harness.AddComparisonPair(reuseOldestBase, reuseOldestTuned);

        _ = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.DecideAsync(secondPending.Id, mixed.Id, secondPending.Version));
        var unchanged = await harness.Service.BeginRevalidationAsync(secondPending.Id, secondPending.Version);
        var fresh = harness.AddComparisonPair();
        var accepted = await harness.Service.DecideAsync(unchanged.Id, fresh.Id, unchanged.Version);

        AssertEx.Equal(secondPending.Version, unchanged.Version);
        AssertEx.Equal(expected: 2, ArtifactQualityService.ReadDecision(unchanged)!.History.Count);
        var acceptedDecision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(accepted));
        AssertEx.Equal(ArtifactQualityOutcome.Passed, acceptedDecision.Outcome);
        AssertEx.Equal(fresh.Id, acceptedDecision.ComparisonId);
        AssertEx.Equal(expected: 2, acceptedDecision.History.Count);
    }

    [Test]
    public async Task Override_WithoutACompleteFailedDecision_IsRejected()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);

        _ = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.OverrideAsync(harness.Artifact.Id, harness.Artifact.Version, "not applicable"));
    }

    [Test]
    public async Task Override_PassedDecision_IsRejected()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0);
        var passed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        _ = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.OverrideAsync(passed.Id, passed.Version, "not applicable"));
    }

    [Test]
    public async Task Override_IdentityFailureCannotBeOverridden()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, tunedFingerprint: "different");
        var failed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        _ = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.OverrideAsync(failed.Id, failed.Version, "cannot waive identity"));
    }

    [Test]
    public async Task Override_BlankReason_IsRejected()
    {
        var harness = Harness.Create(aggregateDelta: -0.01, kindDelta: 0);
        var failed = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        _ = await AssertEx.ThrowsAsync<TrainingExportRejectedException>(() =>
            harness.Service.OverrideAsync(failed.Id, failed.Version, "  "));
    }

    [Test]
    public async Task Decide_BaseAndTunedExecutionPolicyMismatch_FailsClosed()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, executionMismatch: true);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        var decision = AssertEx.NotNull(ArtifactQualityService.ReadDecision(artifact));
        AssertEx.True(decision.FailureCodes.Contains("ExecutionProvenanceMismatch", StringComparer.Ordinal));
    }

    [Test]
    public async Task Decide_MergedExecutionBytesDoNotMatchArtifact_FailsClosed()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, tunedExecutionSha: new string('e', 64));

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        AssertEx.True(ArtifactQualityService.ReadDecision(artifact)!.FailureCodes.Contains("TunedExecutionIdentityMismatch",
            StringComparer.Ordinal));
    }

    [Test]
    public async Task Decide_AdapterBindsArtifactAndExactBaselineBytes()
    {
        var harness = Harness.Create(aggregateDelta: 0, kindDelta: 0, artifactKind: TrainingArtifactKind.AdapterGguf);

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        AssertEx.Equal(ArtifactQualityOutcome.Passed, ArtifactQualityService.ReadDecision(artifact)!.Outcome);
    }

    [Test]
    public async Task Decide_AdapterBaseBytesDoNotMatchBaseline_FailsClosed()
    {
        var harness = Harness.Create(aggregateDelta: 0,
            kindDelta: 0,
            artifactKind: TrainingArtifactKind.AdapterGguf,
            tunedBaseSha: new string('c', 64));

        var artifact = await harness.Service.DecideAsync(harness.Artifact.Id, harness.Comparison.Id, harness.Artifact.Version);

        AssertEx.True(ArtifactQualityService.ReadDecision(artifact)!.FailureCodes.Contains("TunedExecutionIdentityMismatch",
            StringComparer.Ordinal));
    }

    private sealed class Harness
    {
        private readonly ITrainingEvaluationStore _evaluations;
        private readonly TrainingEvaluationRecord _baseEvaluation;
        private readonly TrainingEvaluationRecord _tunedEvaluation;

        private Harness(ArtifactQualityService service,
            TrainingArtifactRecord artifact,
            TrainingComparisonRecord comparison,
            ITrainingEvaluationStore evaluations,
            TrainingEvaluationRecord baseEvaluation,
            TrainingEvaluationRecord tunedEvaluation)
        {
            Service = service;
            Artifact = artifact;
            Comparison = comparison;
            _evaluations = evaluations;
            _baseEvaluation = baseEvaluation;
            _tunedEvaluation = tunedEvaluation;
        }

        public ArtifactQualityService Service { get; }
        public TrainingArtifactRecord Artifact { get; }
        public TrainingComparisonRecord Comparison { get; }

        public TrainingComparisonRecord AddFreshComparisonPair() =>
            AddComparisonPair();

        public TrainingComparisonRecord AddComparisonPair(bool reuseOldestBase = false, bool reuseOldestTuned = false)
        {
            var freshBase = reuseOldestBase ? _baseEvaluation : _baseEvaluation with { Id = Guid.NewGuid(), ComparisonId = null };
            var freshTuned = reuseOldestTuned ? _tunedEvaluation : _tunedEvaluation with { Id = Guid.NewGuid(), ComparisonId = null };
            var fresh = Comparison with
            {
                Id = Guid.NewGuid(),
                BaseEvaluationRunId = freshBase.Id,
                TunedEvaluationRunId = freshTuned.Id,
                DeltasJson = JsonSerializer.SerializeToUtf8Bytes(
                    ComparisonReportService.ComputeDeltas(freshBase, freshTuned, baseBenchmark: null, tunedBenchmark: null),
                    TrainingJson.Options)
            };
            _ = _evaluations.GetComparisonAsync(fresh.Id, Arg.Any<CancellationToken>()).Returns(fresh);
            _ = _evaluations.GetAsync(freshBase.Id, Arg.Any<CancellationToken>()).Returns(freshBase);
            _ = _evaluations.GetAsync(freshTuned.Id, Arg.Any<CancellationToken>()).Returns(freshTuned);
            return fresh;
        }

        public static Harness Create(double aggregateDelta,
            double kindDelta,
            string? tunedFingerprint = null,
            bool benchmarkRegression = false,
            bool executionMismatch = false,
            string? tunedExecutionSha = null,
            string? tunedBaseSha = null,
            TrainingArtifactKind artifactKind = TrainingArtifactKind.MergedGguf,
            bool currentEvaluationRegressed = false,
            bool storedDeltasTampered = false,
            bool comparisonRunMismatch = false,
            bool evaluationRunMismatch = false)
        {
            var runId = Guid.NewGuid();
            var artifactId = Guid.NewGuid();
            const string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var artifact = new TrainingArtifactRecord(artifactId, runId, artifactKind, "/staged/tuned.gguf", sha, 1,
                TrainingArtifactSmokeState.Passed, null, null, 4, 0, 0);
            var run = new TrainingRunRecord(runId, Guid.NewGuid(), "v1:dataset", 1, ReadOnlyMemory<byte>.Empty, Guid.NewGuid(), "base:Q4_K_M",
                "v1:base", ReadOnlyMemory<byte>.Empty, null, TrainingRunStatus.Succeeded, null, null, null, null, 1, 0, 0,
                TrainingWorkStatus.Succeeded, null);
            var firstSampleId = Guid.NewGuid();
            var secondSampleId = Guid.NewGuid();
            var kindOnlyRegression = aggregateDelta >= 0 && kindDelta < 0;
            var sampleIds = kindOnlyRegression ? new[] { firstSampleId, secondSampleId } : [firstSampleId];
            var membership = JsonSerializer.SerializeToUtf8Bytes(new TrainingEvaluationMembershipV1
            {
                TrainingRunId = runId,
                FreezeId = Guid.NewGuid(),
                DatasetId = run.DatasetId,
                DatasetContentFingerprint = run.DatasetContentFingerprint,
                HoldoutSampleIds = sampleIds
            }, TrainingJson.Options);
            IReadOnlyList<TrainingEvaluationResultEntry> baseResults = kindOnlyRegression
                ?
                [
                    new TrainingEvaluationResultEntry(firstSampleId, "tool", true, "deterministic"),
                    new TrainingEvaluationResultEntry(secondSampleId, "other", false, "deterministic")
                ]
                : [new TrainingEvaluationResultEntry(firstSampleId, "tool", true, "deterministic")];
            IReadOnlyList<TrainingEvaluationResultEntry> tunedResults = kindOnlyRegression
                ?
                [
                    new TrainingEvaluationResultEntry(firstSampleId, "tool", false, "deterministic"),
                    new TrainingEvaluationResultEntry(secondSampleId, "other", true, "deterministic")
                ]
                :
                [
                    new TrainingEvaluationResultEntry(firstSampleId,
                        "tool",
                        aggregateDelta >= 0 && !currentEvaluationRegressed,
                        "deterministic")
                ];
            var baseEvaluation = Evaluation(runId, "base:Q4_K_M", "v1:base", membership, EvaluationModelTargetKind.InstalledModel, null,
                variant: "Cuda", modelSha256: new string('b', 64), entries: baseResults);
            var tunedEvaluation = Evaluation(runId, "tuned.gguf", tunedFingerprint ?? sha, membership,
                EvaluationModelTargetKind.StagedTrainingArtifact,
                artifactId,
                executionMismatch ? "Vulkan" : "Cuda",
                artifactKind == TrainingArtifactKind.AdapterGguf ? tunedBaseSha ?? new string('b', 64) : tunedExecutionSha ?? sha,
                artifactKind == TrainingArtifactKind.AdapterGguf ? tunedExecutionSha ?? sha : null,
                entries: tunedResults);
            if (evaluationRunMismatch)
            {
                tunedEvaluation = tunedEvaluation with { TrainingRunId = Guid.NewGuid() };
            }
            var deltas = ComparisonReportService.ComputeDeltas(baseEvaluation, tunedEvaluation, baseBenchmark: null, tunedBenchmark: null);
            if (currentEvaluationRegressed)
            {
                deltas = deltas with
                {
                    BasePassedCount = 0,
                    TunedPassedCount = 1,
                    BaseAccuracy = 0,
                    TunedAccuracy = 1,
                    AccuracyDelta = 1,
                    PerKind = [new ComparisonKindDeltaV1("tool", 1, 0, 1, 1, 0, 1, 1)]
                };
            }
            else if (storedDeltasTampered)
            {
                deltas = deltas with
                {
                    TunedPassedCount = 0,
                    TunedAccuracy = 0,
                    AccuracyDelta = -1,
                    PerKind = [new ComparisonKindDeltaV1("tool", 1, 1, 1, 0, 1, 0, -1)]
                };
            }

            if (benchmarkRegression)
            {
                deltas = deltas with
                {
                    Benchmark = new ComparisonBenchmarkDeltaV1
                    {
                        TokensPerSecondDelta = -100,
                        UserScoreDelta = -5,
                        JudgeScoreDelta = -5
                    }
                };
            }

            var comparison = new TrainingComparisonRecord(Guid.NewGuid(), "quality", baseEvaluation.Id, tunedEvaluation.Id, null, null,
                comparisonRunMismatch ? Guid.NewGuid() : runId,
                JsonSerializer.SerializeToUtf8Bytes(deltas, TrainingJson.Options), 1, 0, 0);

            var runs = Substitute.For<ITrainingRunStore>();
            _ = runs.GetArtifactAsync(artifactId, Arg.Any<CancellationToken>()).Returns(_ => artifact);
            _ = runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(run);
            _ = runs.SetArtifactQualityDecisionAsync(artifactId, Arg.Any<long>(), Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(),
                    Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    artifact = artifact with
                    {
                        Version = artifact.Version + 1,
                        QualityComparisonId = call.ArgAt<Guid>(2),
                        QualityDecisionJson = call.ArgAt<ReadOnlyMemory<byte>>(3)
                    };
                    return artifact;
                });
            var evaluations = Substitute.For<ITrainingEvaluationStore>();
            _ = evaluations.GetComparisonAsync(comparison.Id, Arg.Any<CancellationToken>()).Returns(comparison);
            _ = evaluations.GetAsync(baseEvaluation.Id, Arg.Any<CancellationToken>()).Returns(baseEvaluation);
            _ = evaluations.GetAsync(tunedEvaluation.Id, Arg.Any<CancellationToken>()).Returns(tunedEvaluation);
            return new Harness(new ArtifactQualityService(runs, evaluations, TimeProvider.System), artifact, comparison, evaluations,
                baseEvaluation, tunedEvaluation);
        }

        private static TrainingEvaluationRecord Evaluation(Guid runId, string name, string fingerprint, ReadOnlyMemory<byte> membership,
            EvaluationModelTargetKind kind,
            Guid? artifactId,
            string variant,
            string modelSha256,
            string? adapterSha256 = null,
            IReadOnlyList<TrainingEvaluationResultEntry>? entries = null)
        {
            var results = entries ?? [];
            return new TrainingEvaluationRecord(Guid.NewGuid(), runId, Guid.NewGuid(), name, fingerprint, Guid.NewGuid(), "v1:dataset", membership,
                TrainingEvaluationStatus.Succeeded, TrainingEvaluationResults.Write(results), results.Count, results.Count,
                results.Count(item => item.Passed), TrainingEvaluationResults.WriteTally(TrainingEvaluationResults.Tally(results)), null, 2, 0, 0,
                TrainingWorkStatus.Succeeded, kind, artifactId,
                JsonSerializer.SerializeToUtf8Bytes(new TrainingEvaluationExecutionProvenanceV1
                {
                    Variant = variant,
                    ExecutableVersion = "v1",
                    ExecutableSha256 = new string('c', 64),
                    ManifestSha256 = new string('c', 64),
                    LaunchProjectionIdentity = "projection",
                    ContextTokens = 4096,
                    LaunchPolicyVersion = 1,
                    ModelSha256 = modelSha256,
                    ModelSizeBytes = 1,
                    AdapterSha256 = adapterSha256,
                    AdapterSizeBytes = adapterSha256 is null ? null : 1
                }, TrainingJson.Options));
        }
    }
}
