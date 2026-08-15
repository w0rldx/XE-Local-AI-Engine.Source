namespace XE_Local_AI_Engine.Tests.Training.Evaluation;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A comparison report caches a pure computation. These tests pin that the cache is honest: what a report stores
///     is exactly what recomputing from the two bound evaluations' persisted results and frozen memberships produces.
/// </summary>
public sealed class ComparisonReportServiceTests
{
    private static readonly Guid DatasetId = Guid.NewGuid();

    [Test]
    public async Task Comparison_BindsEvaluationRunIds_ReproducibleFromMembership()
    {
        var membership = Membership([SampleId(1), SampleId(2), SampleId(3), SampleId(4)]);
        var baseEvaluation = Evaluation("base-model",
            membership,
            [Verdict(1, "tool-call", passed: true), Verdict(2, "tool-call", passed: false), Verdict(3, "no-tool", passed: true),
                Verdict(4, "no-tool", passed: false)]);
        var tunedEvaluation = Evaluation("tuned-model",
            membership,
            [Verdict(1, "tool-call", passed: true), Verdict(2, "tool-call", passed: true), Verdict(3, "no-tool", passed: true),
                Verdict(4, "no-tool", passed: false)]);

        var evaluations = Substitute.For<ITrainingEvaluationStore>();
        _ = evaluations.GetAsync(baseEvaluation.Id, Arg.Any<CancellationToken>()).Returns(baseEvaluation);
        _ = evaluations.GetAsync(tunedEvaluation.Id, Arg.Any<CancellationToken>()).Returns(tunedEvaluation);
        TrainingComparisonInput? captured = null;
        _ = evaluations.CreateComparisonAsync(Arg.Do<TrainingComparisonInput>(input => captured = input), Arg.Any<CancellationToken>())
                       .Returns(callInfo => Report(callInfo.Arg<TrainingComparisonInput>()));

        var service = new ComparisonReportService(evaluations, Substitute.For<ITrainingRunStore>(), Substitute.For<IBenchmarkStore>());
        var report = await service.CreateAsync(new CreateComparisonCommand("base vs tuned", baseEvaluation.Id, tunedEvaluation.Id));

        AssertEx.Equal(baseEvaluation.Id, report.BaseEvaluationRunId);
        AssertEx.Equal(tunedEvaluation.Id, report.TunedEvaluationRunId);

        var stored = AssertEx.NotNull(JsonSerializer.Deserialize<TrainingComparisonDeltasV1>(
                AssertEx.NotNull(captured, "The service must hand the store the deltas it computed.").DeltasJson.Span, TrainingJson.Options),
            "The stored deltas must be readable.");

        // 2/4 versus 3/4 over the SAME frozen membership — the whole reason the two sides are comparable.
        AssertEx.Equal(expected: 0.5d, stored.BaseAccuracy);
        AssertEx.Equal(expected: 0.75d, stored.TunedAccuracy);
        AssertEx.Equal(expected: 0.25d, stored.AccuracyDelta);
        AssertEx.True(stored.AccuracyAvailable);

        var toolCall = AssertEx.NotNull(stored.PerKind.FirstOrDefault(kind => kind.Kind == "tool-call"), "The tool-call kind is reported.");
        AssertEx.Equal(expected: 0.5d, toolCall.BaseAccuracy);
        AssertEx.Equal(expected: 1d, toolCall.TunedAccuracy);
        AssertEx.Equal(expected: 0.5d, toolCall.AccuracyDelta);

        var noTool = AssertEx.NotNull(stored.PerKind.FirstOrDefault(kind => kind.Kind == "no-tool"), "The no-tool kind is reported.");
        AssertEx.Equal(expected: 0d, noTool.AccuracyDelta, "A kind that did not move must report a zero delta, not be omitted.");

        // The reproducibility claim itself: recomputing from what is persisted reproduces what was stored.
        var recomputed = ComparisonReportService.ComputeDeltas(baseEvaluation, tunedEvaluation, baseBenchmark: null, tunedBenchmark: null);
        AssertEx.Equal(JsonSerializer.Serialize(recomputed, TrainingJson.Options), JsonSerializer.Serialize(stored, TrainingJson.Options),
            "A report's deltas must be reproducible from the bound evaluations' persisted results.");
    }

    [Test]
    public void ComputeDeltas_WhenOneSideScoredNothing_MarksAccuracyUnavailable()
    {
        var membership = Membership([SampleId(1)]);
        var scored = Evaluation("tuned-model", membership, [Verdict(1, "tool-call", passed: true)]);
        var empty = Evaluation("base-model", membership, []);

        var deltas = ComparisonReportService.ComputeDeltas(empty, scored, baseBenchmark: null, tunedBenchmark: null);

        // Rendering 0% for a base model that was never installed would read as "it got everything wrong".
        AssertEx.False(deltas.AccuracyAvailable);
        AssertEx.NotNull(deltas.UnavailableReason, "An unavailable section says why.");
        AssertEx.Null(deltas.Benchmark, "With no benchmark pairing there is no benchmark section at all.");
    }

    [Test]
    public async Task Create_WhenAPairedBenchmarkRunIsMissing_IsRejected()
    {
        var membership = Membership([SampleId(1)]);
        var baseEvaluation = Evaluation("base-model", membership, [Verdict(1, "tool-call", passed: true)]);
        var tunedEvaluation = Evaluation("tuned-model", membership, [Verdict(1, "tool-call", passed: true)]);
        var evaluations = Substitute.For<ITrainingEvaluationStore>();
        _ = evaluations.GetAsync(baseEvaluation.Id, Arg.Any<CancellationToken>()).Returns(baseEvaluation);
        _ = evaluations.GetAsync(tunedEvaluation.Id, Arg.Any<CancellationToken>()).Returns(tunedEvaluation);
        var benchmarks = Substitute.For<IBenchmarkStore>();
        _ = benchmarks.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((BenchmarkRunRecord?)null);

        var service = new ComparisonReportService(evaluations, Substitute.For<ITrainingRunStore>(), benchmarks);

        _ = await AssertEx.ThrowsAsync<EvaluationRejectedException>(
            () => service.CreateAsync(new CreateComparisonCommand("paired", baseEvaluation.Id, tunedEvaluation.Id, Guid.NewGuid())),
            "A pairing is validated to exist before it is bound.");
        _ = await evaluations.DidNotReceiveWithAnyArgs().CreateComparisonAsync(default!, default);
    }

    [Test]
    public async Task Suggest_ReadsTheRunsLineage_AndNamesWhatIsMissing()
    {
        var runId = Guid.NewGuid();
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(Run(runId, linkedModelName: "base-model"));
        _ = runs.ListArtifactsAsync(runId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingArtifactRecord>>([Artifact(runId, committed: null)]);
        var evaluations = Substitute.For<ITrainingEvaluationStore>();
        _ = evaluations.ListAsync(runId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingEvaluationRecord>>([]);

        var service = new ComparisonReportService(evaluations, runs, Substitute.For<IBenchmarkStore>());
        var unpromoted = await service.SuggestAsync(runId);

        AssertEx.Equal("base-model", unpromoted.BaseModelName!);
        AssertEx.Null(unpromoted.TunedModelName, "Nothing promoted means no tuned model to evaluate yet.");
        AssertEx.NotNull(unpromoted.UnavailableReason);

        // Promote the artifact and an existing evaluation is matched to it by model name.
        _ = runs.ListArtifactsAsync(runId, Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<TrainingArtifactRecord>>([Artifact(runId, "tuned-model")]);
        var tuned = Evaluation("tuned-model", Membership([SampleId(1)]), [Verdict(1, "tool-call", passed: true)]);
        _ = evaluations.ListAsync(runId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingEvaluationRecord>>([tuned]);

        var complete = await service.SuggestAsync(runId);

        AssertEx.Equal("tuned-model", complete.TunedModelName!);
        AssertEx.Equal(tuned.Id, complete.TunedEvaluationRunId!.Value);
        AssertEx.Null(complete.BaseEvaluationRunId, "The base has not been evaluated yet.");
        AssertEx.Null(complete.UnavailableReason, "With both sides nameable there is nothing to warn about.");
    }

    [Test]
    public async Task Suggest_WhenTheRunHasNoInstalledBase_SaysTheAccuracyComparisonIsUnavailable()
    {
        var runId = Guid.NewGuid();
        var runs = Substitute.For<ITrainingRunStore>();
        _ = runs.GetAsync(runId, Arg.Any<CancellationToken>()).Returns(Run(runId, linkedModelName: null));
        _ = runs.ListArtifactsAsync(runId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingArtifactRecord>>([]);
        var evaluations = Substitute.For<ITrainingEvaluationStore>();
        _ = evaluations.ListAsync(runId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TrainingEvaluationRecord>>([]);

        var service = new ComparisonReportService(evaluations, runs, Substitute.For<IBenchmarkStore>());
        var suggestion = await service.SuggestAsync(runId);

        AssertEx.Null(suggestion.BaseModelName);
        AssertEx.True(suggestion.UnavailableReason!.Contains("accuracy comparison is unavailable", StringComparison.Ordinal));
    }

    private static Guid SampleId(int index) =>
        new($"00000000-0000-0000-0000-{index:D12}");

    private static TrainingEvaluationMembershipV1 Membership(IReadOnlyList<Guid> sampleIds) =>
        new()
        {
            TrainingRunId = Guid.NewGuid(),
            FreezeId = Guid.NewGuid(),
            DatasetId = DatasetId,
            DatasetContentFingerprint = "v1:" + new string('a', count: 64),
            HoldoutSampleIds = sampleIds
        };

    private static TrainingEvaluationResultEntry Verdict(int index, string kind, bool passed) =>
        new(SampleId(index), kind, passed, "deterministic");

    private static TrainingEvaluationRecord Evaluation(string modelName,
        TrainingEvaluationMembershipV1 membership,
        IReadOnlyList<TrainingEvaluationResultEntry> entries) =>
        new(Guid.NewGuid(),
            membership.TrainingRunId,
            ComparisonId: null,
            modelName,
            ModelContentFingerprint: null,
            DatasetId,
            membership.DatasetContentFingerprint,
            JsonSerializer.SerializeToUtf8Bytes(membership, TrainingJson.Options),
            TrainingEvaluationStatus.Succeeded,
            entries.Count == 0 ? null : TrainingEvaluationResults.Write(entries),
            membership.HoldoutSampleIds.Count,
            entries.Count,
            entries.Count(entry => entry.Passed),
            TrainingEvaluationResults.WriteTally(TrainingEvaluationResults.Tally(entries)),
            ErrorMessage: null,
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            TrainingWorkStatus.Succeeded);

    private static TrainingComparisonRecord Report(TrainingComparisonInput input) =>
        new(Guid.NewGuid(),
            input.Name,
            input.BaseEvaluationRunId,
            input.TunedEvaluationRunId,
            input.BaseBenchmarkRunId,
            input.TunedBenchmarkRunId,
            input.TrainingRunId,
            input.DeltasJson,
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1);

    private static TrainingRunRecord Run(Guid runId, string? linkedModelName) =>
        new(runId,
            DatasetId,
            "v1:" + new string('a', count: 64),
            DatasetRevision: 1,
            FreezeJson: JsonSerializer.SerializeToUtf8Bytes(new TrainingRunFreezeV1(), TrainingJson.Options),
            BaseArtifactId: Guid.NewGuid(),
            linkedModelName,
            LinkedModelContentFingerprint: null,
            OptionsJson: JsonSerializer.SerializeToUtf8Bytes(new TrainingRunOptionsV1(), TrainingJson.Options),
            LicenseConfirmationJson: null,
            TrainingRunStatus.Succeeded,
            ProgressJson: null,
            LogTail: null,
            LaunchReceiptJson: null,
            ErrorMessage: null,
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            TrainingWorkStatus.Succeeded,
            WorkErrorMessage: null);

    private static TrainingArtifactRecord Artifact(Guid runId, string? committed) =>
        new(Guid.NewGuid(),
            runId,
            TrainingArtifactKind.AdapterGguf,
            "adapter.gguf",
            Sha256: null,
            SizeBytes: 0,
            TrainingArtifactSmokeState.Passed,
            SmokeReason: null,
            committed,
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1);
}
