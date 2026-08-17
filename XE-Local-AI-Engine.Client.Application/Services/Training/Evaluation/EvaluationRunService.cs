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
///         <strong>A drifted dataset is refused, not scored.</strong> Scoring reads the LIVE sample rows — the frozen
///         JSONL is the export format and carries no sample ids, so it cannot answer a hold-out lookup. A review verb
///         that mutates a sample bumps the dataset's <c>ContentFingerprint</c>, so two evaluations created on opposite
///         sides of an edit would silently answer different questions while the comparison treated them as the same
///         frozen membership. Both this service (at create) and <see cref="EvaluationRunExecutor" /> (at scoring, since
///         the dataset can move in between and again on a resume) refuse with
///         <see cref="DriftedDatasetReason" /> rather than produce a number nobody can compare.
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

public sealed class EvaluationRunService(
    ITrainingEvaluationStore evaluations,
    ITrainingRunStore runs,
    ITrainingDatasetStore datasets,
    IGgufModelStore models,
    TrainingRunCancellationRegistry cancellations,
    ITrainingRunQueueSignal signal) : IEvaluationRunService
{
    /// <summary>
    ///     The single operator-facing reason a drifted dataset is refused. Shared with
    ///     <see cref="EvaluationRunExecutor" /> so the create-time 400 and the scoring-time <c>errorMessage</c> the
    ///     frontend renders are the same sentence.
    /// </summary>
    public const string DriftedDatasetReason =
        "The dataset was edited after this run froze its hold-out set, so the scores would not be comparable. "
        + "Re-run the training run to re-freeze the dataset, or evaluate a run made from the current dataset.";

    private readonly TrainingRunCancellationRegistry _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
    private readonly ITrainingDatasetStore _datasets = datasets ?? throw new ArgumentNullException(nameof(datasets));
    private readonly ITrainingEvaluationStore _evaluations = evaluations ?? throw new ArgumentNullException(nameof(evaluations));
    private readonly IGgufModelStore _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly ITrainingRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly ITrainingRunQueueSignal _signal = signal ?? throw new ArgumentNullException(nameof(signal));

    public async Task<TrainingEvaluationRecord> CreateAsync(CreateEvaluationCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var run = await _runs.GetAsync(command.TrainingRunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new EvaluationRejectedException("The training run was not found.");

        var freeze = Read<TrainingRunFreezeV1>(run.FreezeJson)
                     ?? throw new EvaluationRejectedException("The training run's frozen membership could not be read.");
        if (freeze.HoldoutSampleIds.Count == 0)
        {
            throw new EvaluationRejectedException("The training run held nothing back, so there is nothing to evaluate against.");
        }

        var dataset = await _datasets.GetDatasetAsync(run.DatasetId, cancellationToken).ConfigureAwait(false)
                      ?? throw new EvaluationRejectedException("The dataset this run trained on no longer exists.");
        if (!string.Equals(dataset.ContentFingerprint, freeze.DatasetContentFingerprint, StringComparison.Ordinal))
        {
            throw new EvaluationRejectedException(DriftedDatasetReason);
        }

        var modelName = await ResolveModelNameAsync(run, command, cancellationToken).ConfigureAwait(false);
        var installed = await _models.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = installed.FirstOrDefault(model => string.Equals(model.ModelName, modelName, StringComparison.Ordinal) && model.IsAvailable)
                         ?? throw new EvaluationRejectedException($"'{modelName}' is not an installed model on this node.");

        var membership = new TrainingEvaluationMembershipV1
        {
            TrainingRunId = run.Id,
            FreezeId = freeze.FreezeId,
            DatasetId = run.DatasetId,
            DatasetContentFingerprint = run.DatasetContentFingerprint,
            HoldoutSampleIds = freeze.HoldoutSampleIds
        };
        var created = await _evaluations.CreateAndEnqueueAsync(new TrainingEvaluationEnqueueCommand(run.Id,
                                                modelName,
                                                descriptor.ModelContentFingerprint,
                                                run.DatasetId,
                                                run.DatasetContentFingerprint,
                                                JsonSerializer.SerializeToUtf8Bytes(membership, TrainingJson.Options),
                                                freeze.HoldoutSampleIds.Count),
                                            cancellationToken)
                                        .ConfigureAwait(false);
        _signal.Wake();
        return created;
    }

    public Task<IReadOnlyList<TrainingEvaluationRecord>> ListAsync(Guid? trainingRunId, CancellationToken cancellationToken = default) =>
        _evaluations.ListAsync(trainingRunId, cancellationToken);

    public Task<TrainingEvaluationRecord?> GetAsync(Guid evaluationId, CancellationToken cancellationToken = default) =>
        _evaluations.GetAsync(evaluationId, cancellationToken);

    public async Task<TrainingEvaluationRecord> ResumeAsync(Guid evaluationId, CancellationToken cancellationToken = default)
    {
        var evaluation = await _evaluations.GetAsync(evaluationId, cancellationToken).ConfigureAwait(false)
                         ?? throw new EvaluationRejectedException("The evaluation run was not found.");
        var resumed = await _evaluations.ResumeAsync(evaluationId, evaluation.Version, cancellationToken).ConfigureAwait(false);
        _signal.Wake();
        return resumed;
    }

    public async Task<bool> CancelAsync(Guid evaluationId, CancellationToken cancellationToken = default)
    {
        var evaluation = await _evaluations.GetAsync(evaluationId, cancellationToken).ConfigureAwait(false);
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

        _ = await _evaluations.CompleteAsync(evaluationId, TrainingWorkStatus.Cancelled, "Cancelled before the evaluation started.", cancellationToken)
                              .ConfigureAwait(false);
        _signal.Wake();
        return true;
    }

    public Task DeleteAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _evaluations.DeleteAsync(evaluationId, expectedVersion, cancellationToken);

    private async Task<string> ResolveModelNameAsync(TrainingRunRecord run, CreateEvaluationCommand command, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(command.ModelNameOverride))
        {
            return command.ModelNameOverride.Trim();
        }

        if (command.Target == EvaluationTarget.Base)
        {
            // A run started from a raw Hugging Face checkpoint has no installed GGUF counterpart to evaluate, so the
            // comparison's accuracy section is marked unavailable rather than silently compared against nothing.
            return run.LinkedInstalledModelName
                   ?? throw new EvaluationRejectedException("This run was not started from an installed model, so its base model cannot be evaluated.");
        }

        var artifacts = await _runs.ListArtifactsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        return artifacts.Select(artifact => artifact.CommittedModelName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
               ?? throw new EvaluationRejectedException("No artifact from this run has been promoted to the registry yet.");
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
