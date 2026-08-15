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
