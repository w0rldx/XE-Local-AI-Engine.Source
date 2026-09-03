namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>Why an exclusive profiling run refused to start.</summary>
public enum LlamaServerProfilingRefusalReason
{
    /// <summary>A warm role for the model is serving in-flight inference.</summary>
    InUse = 0,

    /// <summary>A teardown (an operator eject, or a cap-admission eviction) already owns that role's process.</summary>
    EvictionAlreadyInProgress = 1
}

/// <summary>
///     Thrown when an exclusive profiling run refuses to start because a warm role for the model could not be claimed:
///     profiling's pre-spawn eviction claims its targets through <c>RunningProcess.TryBeginEvict</c> and never tears
///     down a leased process, so a measurement is skipped rather than taken by killing a live generation.
/// </summary>
/// <remarks>
///     Callers surface this as a distinct SKIPPED outcome, not a failure. <see cref="Exception.Message" /> is the
///     sanitized, operator-facing sentence — a model name, a role, a request count, and what to do next.
/// </remarks>
public sealed class LlamaServerProfilingRefusedException : Exception
{
    /// <summary>Creates a refusal naming the model, the role that refused the claim, and why.</summary>
    public LlamaServerProfilingRefusedException(string modelName,
        ModelRole role,
        int activeLeases,
        LlamaServerProfilingRefusalReason reason)
        : base(BuildMessage(modelName, role, activeLeases, reason))
    {
        ModelName = modelName;
        Role = role;
        ActiveLeases = activeLeases;
        Reason = reason;
    }

    /// <summary>The model whose profiling run was skipped.</summary>
    public string ModelName { get; }

    /// <summary>The warm role that refused the pre-spawn eviction claim.</summary>
    public ModelRole Role { get; }

    /// <summary>
    ///     How many in-flight inference requests that role held, sampled in the refusal branch — 0 by construction when
    ///     <see cref="Reason" /> is <see cref="LlamaServerProfilingRefusalReason.EvictionAlreadyInProgress" />, since
    ///     that claim lost to another teardown rather than to a lease.
    /// </summary>
    public int ActiveLeases { get; }

    /// <summary>Which of the two claim failures happened.</summary>
    public LlamaServerProfilingRefusalReason Reason { get; }

    private static string BuildMessage(string modelName, ModelRole role, int activeLeases, LlamaServerProfilingRefusalReason reason)
    {
        var cause = reason == LlamaServerProfilingRefusalReason.EvictionAlreadyInProgress
            ? "is already being torn down"
            : $"is serving {activeLeases} in-flight request(s)";
        return $"Skipped: {modelName} ({role}) {cause}; profiling did not run and nothing was evicted. Retry when the model is idle.";
    }
}
