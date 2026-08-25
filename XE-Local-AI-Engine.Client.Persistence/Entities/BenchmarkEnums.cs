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
    Judge,

    /// <summary>
    ///     A quant-fidelity measurement: one llama-perplexity child process against the run's frozen placement, with
    ///     no llama-server and therefore no readiness probe. Appended at the END — the ordinal is persisted.
    /// </summary>
    Fidelity,

    /// <summary>A pairwise judging of two runs in one presentation order. Appended at the END.</summary>
    Comparison
}

public enum BenchmarkWorkStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
///     What a repeat group is measuring. The mode changes only SAMPLING, never the launch, so every run of a group
///     still shares one <c>LaunchIdentity</c> and stays comparable as a launch.
/// </summary>
public enum BenchmarkRepeatMode
{
    /// <summary>
    ///     Cold-launch throughput jitter: temperature 0 and one fixed seed, so every repeat produces the SAME answer
    ///     and what varies is only what the machine did. The historical behaviour and the default.
    /// </summary>
    Throughput,

    /// <summary>
    ///     Answer variance: a non-zero temperature and a seed that advances with the repeat index, so the repeats
    ///     differ in exactly one input and the spread of answers is the measurement. Throughput numbers from such a
    ///     group are still real, but they are no longer a controlled comparison.
    /// </summary>
    AnswerVariance
}
