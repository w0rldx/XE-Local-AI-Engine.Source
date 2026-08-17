namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public enum BenchmarkPrimaryStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    CancelRequested,
    Cancelled
}

/// <summary>The lifecycle of one judge attempt. The run-level <see cref="BenchmarkJudgeStatus" /> keeps the states
///     that describe a run rather than a judging (Disabled/Pending/Skipped).</summary>
public enum BenchmarkJudgeAttemptStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum BenchmarkWorkKind
{
    Primary,
    Judge
}

public enum BenchmarkWorkStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}
