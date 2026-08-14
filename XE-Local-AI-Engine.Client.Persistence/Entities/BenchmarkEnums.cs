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

public enum BenchmarkJudgeStatus
{
    Disabled,
    Pending,
    Skipped,
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
