namespace XE_Local_AI_Engine.Client.Services.Training.Evaluation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Creation, listing, resume and cancellation of evaluation runs.
/// </summary>
/// <remarks>
///     <para>
///         Creation is where the membership is copied: BOTH sides of a comparison take the hold-out sample ids from the
///         SAME training run's freeze, so the base model and the tuned model answer exactly the same questions. Deriving
///         a fresh split per side would make the two accuracies incomparable while looking like they compared something.
///     </para>
///     <para>
///         Scoring replays the encrypted, run-owned frozen corpus rather than live dataset rows. Stable sample ids in
///         that corpus bind the membership to the exact trajectories that were present when the run was created, so a
///         later review edit cannot alter or prevent an evaluation of that run.
///     </para>
/// </remarks>
public interface IEvaluationRunService
{
    Task<TrainingEvaluationRecord> CreateAsync(CreateEvaluationCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingEvaluationRecord>> ListAsync(Guid? trainingRunId, CancellationToken cancellationToken = default);

    Task<TrainingEvaluationRecord?> GetAsync(Guid evaluationId, CancellationToken cancellationToken = default);

    /// <summary>Re-queues an interrupted evaluation; the executor continues at the next unscored sample.</summary>
    Task<TrainingEvaluationRecord> ResumeAsync(Guid evaluationId, CancellationToken cancellationToken = default);

    /// <summary>True when something was cancelled, false when the evaluation is unknown or already terminal.</summary>
    Task<bool> CancelAsync(Guid evaluationId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default);
}

public sealed class EvaluationRunService : IEvaluationRunService
{
    private readonly TrainingRunCancellationRegistry _cancellations;
    private readonly ITrainingDatasetStore _datasets;
    private readonly ITrainingEvaluationStore _evaluations;
    private readonly IGgufModelStore _models;
    private readonly ITrainingRunStore _runs;
    private readonly ITrainingRunQueueSignal _signal;

    public EvaluationRunService(
        ITrainingEvaluationStore evaluations,
        ITrainingRunStore runs,
        ITrainingDatasetStore datasets,
        IGgufModelStore models,
        TrainingRunCancellationRegistry cancellations,
        ITrainingRunQueueSignal signal)
    {
        ArgumentNullException.ThrowIfNull(cancellations);
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(evaluations);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(signal);
        _cancellations = cancellations;
        _datasets = datasets;
        _evaluations = evaluations;
        _models = models;
        _runs = runs;
        _signal = signal;
    }

    public async Task<TrainingEvaluationRecord> CreateAsync(CreateEvaluationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var run = await _runs.GetAsync(command.TrainingRunId, cancellationToken)
                  ?? throw new EvaluationRejectedException("The training run was not found.");

        var freeze = Read<TrainingRunFreezeV1>(run.FreezeJson)
                     ?? throw new EvaluationRejectedException("The training run's frozen membership could not be read.");
        if (freeze.HoldoutSampleIds.Count == 0)
        {
            throw new EvaluationRejectedException("The training run held nothing back, so there is nothing to evaluate against.");
        }

        _ = await _datasets.GetDatasetAsync(run.DatasetId, cancellationToken)
            ?? throw new EvaluationRejectedException("The dataset this run trained on no longer exists.");

        var target = await ResolveTargetAsync(run, command, cancellationToken);

        var membership = new TrainingEvaluationMembershipV1
        {
            TrainingRunId = run.Id,
            FreezeId = freeze.FreezeId,
            DatasetId = run.DatasetId,
            DatasetContentFingerprint = run.DatasetContentFingerprint,
            HoldoutSampleIds = freeze.HoldoutSampleIds
        };
        var created = await _evaluations.CreateAndEnqueueAsync(new TrainingEvaluationEnqueueCommand
        {
            TrainingRunId = run.Id,
            ModelName = target.ModelName,
            ModelContentFingerprint = target.Fingerprint,
            DatasetId = run.DatasetId,
            DatasetContentFingerprint = run.DatasetContentFingerprint,
            MembershipJson = JsonSerializer.SerializeToUtf8Bytes(membership, TrainingJson.Options),
            TotalCount = freeze.HoldoutSampleIds.Count,
            TargetKind = target.Kind,
            SourceArtifactId = target.ArtifactId
        },
                                            cancellationToken);
        _signal.Wake();
        return created;
    }

    public Task<IReadOnlyList<TrainingEvaluationRecord>> ListAsync(Guid? trainingRunId, CancellationToken cancellationToken = default) =>
        _evaluations.ListAsync(trainingRunId, cancellationToken);

    public Task<TrainingEvaluationRecord?> GetAsync(Guid evaluationId, CancellationToken cancellationToken = default) =>
        _evaluations.GetAsync(evaluationId, cancellationToken);

    public async Task<TrainingEvaluationRecord> ResumeAsync(Guid evaluationId, CancellationToken cancellationToken = default)
    {
        var evaluation = await _evaluations.GetAsync(evaluationId, cancellationToken)
                         ?? throw new EvaluationRejectedException("The evaluation run was not found.");
        var resumed = await _evaluations.ResumeAsync(evaluationId, evaluation.Version, cancellationToken);
        _signal.Wake();
        return resumed;
    }

    public async Task<bool> CancelAsync(Guid evaluationId, CancellationToken cancellationToken = default)
    {
        var evaluation = await _evaluations.GetAsync(evaluationId, cancellationToken);
        if (evaluation is null
            || evaluation.Status is TrainingEvaluationStatus.Succeeded or TrainingEvaluationStatus.Failed or TrainingEvaluationStatus.Cancelled)
        {
            return false;
        }

        // A running evaluation is signalled, never terminalized here: the executor owns the terminal write, so
        // cancelling from two places would race the work item.
        if (_cancellations.Cancel(evaluationId))
        {
            return true;
        }

        _ = await _evaluations.CompleteAsync(evaluationId, TrainingWorkStatus.Cancelled, "Cancelled before the evaluation started.", cancellationToken);
        _signal.Wake();
        return true;
    }

    public Task DeleteAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _evaluations.DeleteAsync(evaluationId, expectedVersion, cancellationToken);

    private async Task<EvaluationTargetIdentity> ResolveTargetAsync(TrainingRunRecord run,
        CreateEvaluationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Target == EvaluationTarget.Undefined || !Enum.IsDefined(command.Target))
        {
            throw new EvaluationRejectedException("An evaluation target is required.");
        }

        if (command.Target == EvaluationTarget.Tuned)
        {
            var artifactId = command.ArtifactId
                             ?? throw new EvaluationRejectedException("A staged artifact id is required for tuned evaluation.");
            var artifact = await _runs.GetArtifactAsync(artifactId, cancellationToken)
                           ?? throw new EvaluationRejectedException("The staged artifact was not found.");
            if (artifact.RunId != run.Id || artifact.DiscardedAtUtc is not null || artifact.Kind == TrainingArtifactKind.HfAdapterDir
                || !File.Exists(artifact.Path)
                || string.IsNullOrWhiteSpace(artifact.Sha256))
            {
                throw new EvaluationRejectedException("The tuned evaluation requires a completed staged GGUF from this run.");
            }

            return new EvaluationTargetIdentity
            {
                ModelName = Path.GetFileName(artifact.Path),
                Fingerprint = artifact.Sha256,
                Kind = EvaluationModelTargetKind.StagedTrainingArtifact,
                ArtifactId = artifact.Id
            };
        }

        var selected = string.IsNullOrWhiteSpace(command.ModelNameOverride) ? run.LinkedInstalledModelName : command.ModelNameOverride.Trim();
        var modelName = selected
                        ?? throw new EvaluationRejectedException("This run was not started from an installed model, so its base model cannot be evaluated.");
        var installed = await _models.ListInstalledModelsAsync(cancellationToken);
        var descriptor = installed.FirstOrDefault(model => string.Equals(model.ModelName, modelName, StringComparison.Ordinal) && model.IsAvailable)
                         ?? throw new EvaluationRejectedException($"'{modelName}' is not an installed model on this node.");
        return new EvaluationTargetIdentity
        {
            ModelName = modelName,
            Fingerprint = descriptor.ModelContentFingerprint,
            Kind = EvaluationModelTargetKind.InstalledModel,
            ArtifactId = null
        };
    }

    private sealed record EvaluationTargetIdentity
    {
        public required string ModelName { get; init; }

        public required string? Fingerprint { get; init; }

        public required EvaluationModelTargetKind Kind { get; init; }

        public required Guid? ArtifactId { get; init; }
    }

    private static T? Read<T>(ReadOnlyMemory<byte>? payload)
        where T : class
    {
        if (payload is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
