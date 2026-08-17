namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>Dataset shape. v1 ships tool-calling only; the seam stays open for later kinds.</summary>
public enum TrainingDatasetKind
{
    ToolCalling
}

public enum TrainingDatasetStatus
{
    Generating,
    Ready,
    Failed
}

/// <summary>Whether a sample demonstrates the behaviour to imitate or the behaviour to avoid.</summary>
public enum TrainingSampleLabel
{
    Good,
    Bad
}

/// <summary>Staged-inert review flag — a generated sample is <c>Pending</c> until an operator acts on it.</summary>
public enum TrainingSampleReviewState
{
    Pending,
    Approved,
    Rejected
}

/// <summary>How the sample entered the dataset — the <see cref="GoldenConversationSource" /> analogue.</summary>
public enum TrainingSampleProvenance
{
    Generated,
    Manual
}

public enum ToolMockVerificationState
{
    Unverified,
    Verified,
    Rejected
}

public enum TrainingBaseArtifactStatus
{
    Downloading,
    Ready,
    Failed
}

public enum DatasetGenerationWorkStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
///     Lifecycle of one training run. The four middle states are the executor's own progression; the last three are
///     terminal and only <c>ITrainingRunStore.CompleteRunAsync</c> writes them.
/// </summary>
public enum TrainingRunStatus
{
    Queued,
    Preparing,
    Training,
    Exporting,
    Smoke,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
///     Lifecycle of one evaluation run. Flatter than a training run's: an evaluation has no preparation or export
///     phase, it scores samples one at a time and its progress is the scored count rather than a named phase.
/// </summary>
public enum TrainingEvaluationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum EvaluationModelTargetKind
{
    InstalledModel,
    StagedTrainingArtifact
}

/// <summary>What a <c>TrainingWorkItem</c> row's target id points at.</summary>
public enum TrainingWorkKind
{
    TrainingRun,
    EvaluationRun
}

public enum TrainingWorkStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>What a run produced. All three are staged under the run's own directory until promotion.</summary>
public enum TrainingArtifactKind
{
    AdapterGguf,
    MergedGguf,
    HfAdapterDir
}

/// <summary>
///     Outcome of the post-export smoke load. <see cref="Skipped" /> is a deliberate operator choice, not a silent
///     pass — an artifact only reaches the registry from <see cref="Passed" /> or an explicit <see cref="Skipped" />.
/// </summary>
public enum TrainingArtifactSmokeState
{
    Pending,
    Passed,
    Failed,
    Skipped
}
