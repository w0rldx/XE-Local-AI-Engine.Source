namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;

public enum ArtifactQualityOutcome
{
    Pending,
    Passed,
    Failed,
    Overridden
}

public sealed record ArtifactQualityDecisionAuditV1
{
    public Guid ArtifactId { get; init; }
    public string ArtifactSha256 { get; init; } = string.Empty;
    public Guid ComparisonId { get; init; }
    public Guid BaseEvaluationId { get; init; }
    public Guid TunedEvaluationId { get; init; }
    public int PolicyVersion { get; init; }
    public double MinimumAggregateDelta { get; init; }
    public double MinimumPerKindDelta { get; init; }
    public ArtifactQualityOutcome Outcome { get; init; }
    public IReadOnlyList<string> FailureCodes { get; init; } = [];
    public long DecidedAtUtc { get; init; }
    public string? OverrideReason { get; init; }
    public long? OverriddenAtUtc { get; init; }
}

public sealed record ArtifactQualityDecisionV1
{
    public const int CurrentPolicyVersion = 1;
    public int SchemaVersion { get; init; } = 1;
    public int PolicyVersion { get; init; } = CurrentPolicyVersion;
    public Guid ArtifactId { get; init; }
    public string ArtifactSha256 { get; init; } = string.Empty;
    public Guid ComparisonId { get; init; }
    public Guid BaseEvaluationId { get; init; }
    public Guid TunedEvaluationId { get; init; }
    public ArtifactQualityOutcome Outcome { get; init; }
    public IReadOnlyList<string> FailureCodes { get; init; } = [];
    public double MinimumAggregateDelta { get; init; }
    public double MinimumPerKindDelta { get; init; }
    public long DecidedAtUtc { get; init; }
    public string? OverrideReason { get; init; }
    public long? OverriddenAtUtc { get; init; }
    public IReadOnlyList<ArtifactQualityDecisionAuditV1> History { get; init; } = [];
}

public interface IArtifactQualityService
{
    Task<TrainingArtifactRecord> DecideAsync(Guid artifactId, Guid comparisonId, long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<TrainingArtifactRecord> BeginRevalidationAsync(Guid artifactId, long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<TrainingArtifactRecord> OverrideAsync(Guid artifactId, long expectedVersion, string reason,
        CancellationToken cancellationToken = default);
}

public sealed class ArtifactQualityService(ITrainingRunStore runs, ITrainingEvaluationStore evaluations, TimeProvider timeProvider)
    : IArtifactQualityService
{
    public const string RevalidationEvidenceReusedCode = "RevalidationEvidenceReused";
    private const int MaxDecisionHistoryEntries = 64;
    private readonly ITrainingEvaluationStore _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
    private readonly ITrainingRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<TrainingArtifactRecord> DecideAsync(Guid artifactId, Guid comparisonId, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var artifact = await _runs.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingExportRejectedException("The artifact was not found.");
        if (artifact.Version != expectedVersion)
        {
            throw new TrainingConflictException("VersionConflict");
        }

        if (string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            throw new TrainingExportRejectedException("The artifact has no completed content digest.");
        }

        var prior = ReadDecision(artifact);
        if (prior is not null && prior.Outcome != ArtifactQualityOutcome.Pending)
        {
            throw new TrainingExportRejectedException("Begin quality revalidation before replacing a completed decision.");
        }

        var run = await _runs.GetAsync(artifact.RunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new TrainingExportRejectedException("The run behind this artifact was not found.");
        if (string.IsNullOrWhiteSpace(run.LinkedInstalledModelName) || string.IsNullOrWhiteSpace(run.LinkedModelContentFingerprint))
        {
            throw new TrainingExportRejectedException("This run has no installed base counterpart, so promotion quality cannot be compared.");
        }

        var comparison = await _evaluations.GetComparisonAsync(comparisonId, cancellationToken).ConfigureAwait(false)
                         ?? throw new TrainingExportRejectedException("The comparison report was not found.");
        var baseEvaluation = await _evaluations.GetAsync(comparison.BaseEvaluationRunId, cancellationToken).ConfigureAwait(false)
                             ?? throw new TrainingExportRejectedException("The base evaluation was not found.");
        var tunedEvaluation = await _evaluations.GetAsync(comparison.TunedEvaluationRunId, cancellationToken).ConfigureAwait(false)
                              ?? throw new TrainingExportRejectedException("The tuned evaluation was not found.");
        if (prior is { Outcome: ArtifactQualityOutcome.Pending, History.Count: > 0 })
        {
            var reusesAuditedEvidence = prior.History.Any(audit => comparison.Id == audit.ComparisonId
                                                                   || baseEvaluation.Id == audit.BaseEvaluationId
                                                                   || tunedEvaluation.Id == audit.TunedEvaluationId);
            if (reusesAuditedEvidence)
            {
                throw new TrainingExportRejectedException($"{RevalidationEvidenceReusedCode}: Quality revalidation requires a fresh comparison and fresh base and tuned evaluations.");
            }
        }

        var failures = ValidateEvidence(artifact, run, comparison, baseEvaluation, tunedEvaluation);
        var storedDeltas = ReadDeltas(comparison);
        var currentDeltas = ComparisonReportService.ComputeDeltas(baseEvaluation, tunedEvaluation, baseBenchmark: null, tunedBenchmark: null);
        if (!MatchesEvaluationDeltas(storedDeltas, currentDeltas))
        {
            failures.Add("ComparisonDeltasMismatch");
        }

        // The persisted report is a cache, not authority. Policy is always applied to a fresh computation from the
        // bound evaluations so an appended/resumed result can never preserve an earlier, more favorable verdict.
        if (!currentDeltas.AccuracyAvailable || currentDeltas.AccuracyDelta < 0)
        {
            failures.Add("AggregateRegression");
        }

        if (currentDeltas.PerKind.Any(item => item.AccuracyDelta < 0))
        {
            failures.Add("PerKindRegression");
        }

        var decision = new ArtifactQualityDecisionV1
        {
            ArtifactId = artifact.Id,
            ArtifactSha256 = artifact.Sha256,
            ComparisonId = comparison.Id,
            BaseEvaluationId = baseEvaluation.Id,
            TunedEvaluationId = tunedEvaluation.Id,
            Outcome = failures.Count == 0 ? ArtifactQualityOutcome.Passed : ArtifactQualityOutcome.Failed,
            FailureCodes = failures,
            DecidedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            History = prior?.History ?? []
        };
        return await _runs.SetArtifactQualityDecisionAsync(artifact.Id, expectedVersion, comparison.Id,
                              JsonSerializer.SerializeToUtf8Bytes(decision, TrainingJson.Options), cancellationToken)
                          .ConfigureAwait(false);
    }

    public async Task<TrainingArtifactRecord> BeginRevalidationAsync(Guid artifactId, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var artifact = await _runs.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingExportRejectedException("The artifact was not found.");
        if (artifact.Version != expectedVersion)
        {
            throw new TrainingConflictException("VersionConflict");
        }

        if (artifact.CommittedModelName is not null || artifact.DiscardedAtUtc is not null)
        {
            throw new TrainingExportRejectedException("Only a staged artifact can be revalidated.");
        }

        var current = ReadDecision(artifact)
                      ?? throw new TrainingExportRejectedException("A completed quality decision is required before revalidation.");
        if (current.Outcome == ArtifactQualityOutcome.Pending)
        {
            return artifact;
        }

        if (current.History.Count >= MaxDecisionHistoryEntries)
        {
            throw new TrainingExportRejectedException("The quality decision audit history is full; retain the artifact as-is.");
        }

        var history = current.History.Append(ToAudit(current)).ToArray();
        var pending = current with
        {
            Outcome = ArtifactQualityOutcome.Pending,
            FailureCodes = ["RevalidationPending"],
            DecidedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            OverrideReason = null,
            OverriddenAtUtc = null,
            History = history
        };
        return await _runs.SetArtifactQualityDecisionAsync(artifact.Id, expectedVersion, current.ComparisonId,
                              JsonSerializer.SerializeToUtf8Bytes(pending, TrainingJson.Options), cancellationToken)
                          .ConfigureAwait(false);
    }

    public async Task<TrainingArtifactRecord> OverrideAsync(Guid artifactId, long expectedVersion, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new TrainingExportRejectedException("An audited override reason is required.");
        }

        if (reason.Trim().Length > 1024)
        {
            throw new TrainingExportRejectedException("An audited override reason cannot exceed 1024 characters.");
        }

        var artifact = await _runs.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingExportRejectedException("The artifact was not found.");
        if (artifact.Version != expectedVersion)
        {
            throw new TrainingConflictException("VersionConflict");
        }

        var current = ReadDecision(artifact)
                      ?? throw new TrainingExportRejectedException("A complete failed quality comparison is required before an override.");
        if (current.Outcome != ArtifactQualityOutcome.Failed || artifact.QualityComparisonId is not { } comparisonId)
        {
            throw new TrainingExportRejectedException("Only a complete failed quality comparison can be overridden.");
        }

        if (current.FailureCodes.Count == 0
            || current.FailureCodes.Any(code => code is not ("AggregateRegression" or "PerKindRegression")))
        {
            throw new TrainingExportRejectedException("Only a complete comparison that failed the regression policy can be overridden.");
        }

        if (!string.Equals(current.ArtifactSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrainingExportRejectedException("The staged artifact changed after its quality decision.");
        }

        var overridden = current with
        {
            Outcome = ArtifactQualityOutcome.Overridden,
            OverrideReason = reason.Trim(),
            OverriddenAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
        return await _runs.SetArtifactQualityDecisionAsync(artifact.Id, expectedVersion, comparisonId,
                              JsonSerializer.SerializeToUtf8Bytes(overridden, TrainingJson.Options), cancellationToken)
                          .ConfigureAwait(false);
    }

    public static ArtifactQualityDecisionV1? ReadDecision(TrainingArtifactRecord artifact)
    {
        if (artifact.QualityDecisionJson is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ArtifactQualityDecisionV1>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ValidateEvidence(TrainingArtifactRecord artifact, TrainingRunRecord run,
        TrainingComparisonRecord comparison, TrainingEvaluationRecord baseEvaluation, TrainingEvaluationRecord tunedEvaluation)
    {
        var failures = new List<string>();
        if (comparison.TrainingRunId != artifact.RunId)
        {
            failures.Add("ComparisonRunMismatch");
        }

        if (baseEvaluation.TrainingRunId != artifact.RunId || tunedEvaluation.TrainingRunId != artifact.RunId)
        {
            failures.Add("EvaluationRunMismatch");
        }

        if (baseEvaluation.Status != TrainingEvaluationStatus.Succeeded || baseEvaluation.ScoredCount != baseEvaluation.TotalCount)
        {
            failures.Add("BaseEvaluationIncomplete");
        }

        if (tunedEvaluation.Status != TrainingEvaluationStatus.Succeeded || tunedEvaluation.ScoredCount != tunedEvaluation.TotalCount)
        {
            failures.Add("TunedEvaluationIncomplete");
        }

        if (baseEvaluation.TargetKind != EvaluationModelTargetKind.InstalledModel
            || !string.Equals(baseEvaluation.ModelName, run.LinkedInstalledModelName, StringComparison.Ordinal)
            || !string.Equals(baseEvaluation.ModelContentFingerprint, run.LinkedModelContentFingerprint, StringComparison.Ordinal))
        {
            failures.Add("BaseIdentityMismatch");
        }

        if (tunedEvaluation.TargetKind != EvaluationModelTargetKind.StagedTrainingArtifact
            || tunedEvaluation.SourceArtifactId != artifact.Id
            || !string.Equals(tunedEvaluation.ModelContentFingerprint, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("TunedIdentityMismatch");
        }

        var left = ReadMembership(baseEvaluation);
        var right = ReadMembership(tunedEvaluation);
        if (left is null || right is null || left.TrainingRunId != artifact.RunId || right.TrainingRunId != artifact.RunId
            || left.FreezeId != right.FreezeId || left.DatasetId != right.DatasetId
            || !string.Equals(left.DatasetContentFingerprint, right.DatasetContentFingerprint, StringComparison.Ordinal)
            || !left.HoldoutSampleIds.ToHashSet().SetEquals(right.HoldoutSampleIds))
        {
            failures.Add("FrozenMembershipMismatch");
        }

        var baseExecution = ReadExecutionProvenance(baseEvaluation);
        var tunedExecution = ReadExecutionProvenance(tunedEvaluation);
        if (baseExecution is null || tunedExecution is null)
        {
            failures.Add("ExecutionProvenanceMissing");
        }
        else if (!SameExecutionPolicy(baseExecution, tunedExecution))
        {
            failures.Add("ExecutionProvenanceMismatch");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(baseExecution.ModelSha256) || baseExecution.ModelSizeBytes <= 0)
            {
                failures.Add("BaseExecutionIdentityMismatch");
            }

            var tunedIdentityMatches = artifact.Kind switch
            {
                TrainingArtifactKind.MergedGguf =>
                    string.Equals(tunedExecution.ModelSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                    && tunedExecution.ModelSizeBytes == artifact.SizeBytes
                    && tunedExecution.AdapterSha256 is null
                    && tunedExecution.AdapterSizeBytes is null,
                TrainingArtifactKind.AdapterGguf =>
                    string.Equals(tunedExecution.AdapterSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                    && tunedExecution.AdapterSizeBytes == artifact.SizeBytes
                    && string.Equals(tunedExecution.ModelSha256, baseExecution.ModelSha256, StringComparison.OrdinalIgnoreCase)
                    && tunedExecution.ModelSizeBytes == baseExecution.ModelSizeBytes,
                _ => false
            };
            if (!tunedIdentityMatches)
            {
                failures.Add("TunedExecutionIdentityMismatch");
            }
        }

        return failures;
    }

    private static bool SameExecutionPolicy(TrainingEvaluationExecutionProvenanceV1 left,
        TrainingEvaluationExecutionProvenanceV1 right) =>
        string.Equals(left.Variant, right.Variant, StringComparison.Ordinal)
        && string.Equals(left.ExecutableVersion, right.ExecutableVersion, StringComparison.Ordinal)
        && string.Equals(left.ExecutableSha256, right.ExecutableSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.ManifestSha256, right.ManifestSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.LaunchProjectionIdentity, right.LaunchProjectionIdentity, StringComparison.Ordinal)
        && left.ContextTokens == right.ContextTokens
        && left.LaunchPolicyVersion == right.LaunchPolicyVersion
        && left.LaunchPolicyChatCacheReuse == right.LaunchPolicyChatCacheReuse
        && left.LaunchPolicyChatCacheRamMiB == right.LaunchPolicyChatCacheRamMiB
        && left.LaunchPolicySpeculativeDecoding == right.LaunchPolicySpeculativeDecoding;

    private static TrainingEvaluationExecutionProvenanceV1? ReadExecutionProvenance(TrainingEvaluationRecord evaluation)
    {
        if (evaluation.ExecutionProvenanceJson is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TrainingEvaluationExecutionProvenanceV1>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrainingEvaluationMembershipV1? ReadMembership(TrainingEvaluationRecord evaluation)
    {
        try
        {
            return JsonSerializer.Deserialize<TrainingEvaluationMembershipV1>(evaluation.MembershipJson.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrainingComparisonDeltasV1 ReadDeltas(TrainingComparisonRecord comparison)
    {
        try
        {
            return JsonSerializer.Deserialize<TrainingComparisonDeltasV1>(comparison.DeltasJson.Span, TrainingJson.Options)
                   ?? throw new TrainingExportRejectedException("The comparison quality deltas could not be read.");
        }
        catch (JsonException exception)
        {
            throw new TrainingExportRejectedException("The comparison quality deltas could not be read.", exception);
        }
    }

    private static bool MatchesEvaluationDeltas(TrainingComparisonDeltasV1 stored, TrainingComparisonDeltasV1 current) =>
        stored.SchemaVersion == current.SchemaVersion
        && string.Equals(stored.BaseModelName, current.BaseModelName, StringComparison.Ordinal)
        && string.Equals(stored.TunedModelName, current.TunedModelName, StringComparison.Ordinal)
        && stored.BaseScoredCount == current.BaseScoredCount
        && stored.BasePassedCount == current.BasePassedCount
        && stored.TunedScoredCount == current.TunedScoredCount
        && stored.TunedPassedCount == current.TunedPassedCount
        && SameDouble(stored.BaseAccuracy, current.BaseAccuracy)
        && SameDouble(stored.TunedAccuracy, current.TunedAccuracy)
        && SameDouble(stored.AccuracyDelta, current.AccuracyDelta)
        && stored.AccuracyAvailable == current.AccuracyAvailable
        && string.Equals(stored.UnavailableReason, current.UnavailableReason, StringComparison.Ordinal)
        && stored.PerKind.SequenceEqual(current.PerKind);

    private static bool SameDouble(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    private static ArtifactQualityDecisionAuditV1 ToAudit(ArtifactQualityDecisionV1 decision) =>
        new()
        {
            ArtifactId = decision.ArtifactId,
            ArtifactSha256 = decision.ArtifactSha256,
            ComparisonId = decision.ComparisonId,
            BaseEvaluationId = decision.BaseEvaluationId,
            TunedEvaluationId = decision.TunedEvaluationId,
            PolicyVersion = decision.PolicyVersion,
            MinimumAggregateDelta = decision.MinimumAggregateDelta,
            MinimumPerKindDelta = decision.MinimumPerKindDelta,
            Outcome = decision.Outcome,
            FailureCodes = decision.FailureCodes,
            DecidedAtUtc = decision.DecidedAtUtc,
            OverrideReason = decision.OverrideReason,
            OverriddenAtUtc = decision.OverriddenAtUtc
        };
}
