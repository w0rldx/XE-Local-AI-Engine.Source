namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Models;

public static class BenchmarkFrozenPolicies
{
    public const string FixedSeedPolicy = "fixed";

    /// <summary>The frozen sampling every benchmark generation replays.</summary>
    /// <remarks>
    ///     <paramref name="maxOutputTokens" /> is the project's optional output budget (<c>n_predict</c>); the default
    ///     keeps generation context-limited and keeps the judge-policy sampling — which never takes a budget —
    ///     byte-identical to what it has always hashed. The same holds for the two reasoning-budget arguments:
    ///     omitted, they are omitted from the payload entirely.
    /// </remarks>
    public static BenchmarkSamplingSnapshotV1 DeterministicSampling(int? maxOutputTokens = null,
        int? reasoningBudgetTokens = null,
        bool? reasoningBudgetEnforceable = null) =>
        new(0, null, null, null, maxOutputTokens, null, null, null, null, [], FixedSeedPolicy, "0", reasoningBudgetTokens,
            reasoningBudgetEnforceable);

    /// <summary>
    ///     Tokens a project's context must keep clear of its own budgets, so a run that pins both a reasoning budget
    ///     and an output budget still has room for the task, the system prompt and the agent's tool offer.
    /// </summary>
    /// <remarks>
    ///     A coarse floor on purpose: the exact prompt is not known when the project is validated, and a floor that
    ///     refuses the obviously impossible is worth more than a precise one that needs the frozen runtime to compute.
    /// </remarks>
    public const int MinimumPromptReserveTokens = 512;

    /// <summary>The generation budget a run gets when its project does not pin one. See <see cref="FrozenTimeouts" />.</summary>
    public const int DefaultInvocationTimeoutSeconds = 900;

    /// <summary>The bounds an operator-chosen generation budget must sit inside.</summary>
    public const int MinInvocationTimeoutSeconds = 60;

    public const int MaxInvocationTimeoutSeconds = 7200;

    /// <summary>
    ///     The timeout policy a benchmark generation runs under, pinned here because the node-level
    ///     <see cref="TimeoutSettings.InvocationTimeoutSeconds" /> default has since moved.
    /// </summary>
    /// <remarks>
    ///     A frozen run replays identically across app versions instead of inheriting whatever the package builder
    ///     defaults to. Only the invocation budget is operator-tunable — the tool-call and stream-idle budgets bound a
    ///     STALL, not a legitimate answer's length. The default is 900 s, not 300: at 300 a 27B reasoning model was
    ///     cancelled mid-answer at 307 s, so the timeout measured the harness rather than the model. Timeout values
    ///     are NOT part of the versioned configuration hash, so changing a pinned one is not reflected there.
    /// </remarks>
    public static TimeoutSettings FrozenTimeouts(int? invocationTimeoutSeconds = null) =>
        new()
        {
            InvocationTimeoutSeconds = invocationTimeoutSeconds ?? DefaultInvocationTimeoutSeconds,
            ToolCallTimeoutSeconds = 30,
            StreamIdleTimeoutSeconds = 60
        };
}
