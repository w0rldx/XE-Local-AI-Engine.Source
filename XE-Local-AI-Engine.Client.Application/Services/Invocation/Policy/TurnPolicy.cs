namespace XE_Local_AI_Engine.Client.Services.Invocation.Policy;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Immutable, per-turn snapshot of every timeout, retry and budget knob that governs one invocation.
/// </summary>
/// <remarks>
///     Resolved ONCE in <c>InvocationRunner.RunAsync</c> and flowed unchanged through both the single-agent and
///     orchestration paths, so the two enforce identical policy for one turn. A resolution and documentation seam
///     only: every field is copied from an existing configured source — the package's <see cref="TimeoutSettings" />
///     and the <c>Agent:*</c> sections — and nothing here is persisted or part of a config hash. The ladder the three
///     timeouts form, and its two deliberate splits: docs/wiki/04-agent-mode.md §2.5.
/// </remarks>
public sealed record TurnPolicy
{
    public required TimeSpan InvocationTimeout { get; init; }

    public required TimeSpan StreamIdleTimeout { get; init; }

    /// <summary>Fixed, path-free message surfaced when <see cref="StreamIdleTimeout" /> fires.</summary>
    public required string StreamIdleTimeoutMessage { get; init; }

    public required TimeSpan ToolResultTimeout { get; init; }

    public required int ContextCapacityTokens { get; init; }

    /// <summary>
    ///     The raw per-send <c>num_ctx</c> override this turn's <see cref="ContextCapacityTokens" /> came from, or
    ///     <see langword="null" /> when the capacity is the configured default.
    /// </summary>
    /// <remarks>
    ///     <see cref="WithEffectiveContext" /> needs the distinction: a user-requested bound is a ceiling to keep,
    ///     whereas the untrusted node default-context fallback must be
    ///     REPLACED by the window the model actually launched with.
    /// </remarks>
    public int? RequestedContextTokens { get; init; }

    public required int ReservedOutputTokens { get; init; }

    public required int MaxToolIterationsPerRequest { get; init; }

    public required int MaxConsecutiveInvalidToolCallsPerTool { get; init; }

    public required TimeSpan BaseRetryDelay { get; init; }

    public required TimeSpan MaxRetryDelay { get; init; }

    public required bool CircuitBreakerEnabled { get; init; }

    public required int CircuitBreakerFailureThreshold { get; init; }

    public required TimeSpan CircuitBreakerBreakDuration { get; init; }

    /// <summary>
    ///     Resolves the policy for one turn from the package's per-invocation <see cref="TimeoutSettings" /> plus the
    ///     node-level operational options.
    /// </summary>
    /// <param name="fallbackToolResultTimeout">
    ///     The node-global pending tool-call age, used when the package sets no explicit <c>ToolCallTimeoutSeconds</c>.
    /// </param>
    /// <param name="defaultContextTokens">The node's live default window (<c>INodeRuntimeSettings.GetDefaultContextTokensAsync</c>), read once per turn.</param>
    public static TurnPolicy Resolve(RuntimePackage package,
        ConversationContextBudgetOptions budgetOptions,
        ProviderResilienceOptions resilienceOptions,
        AgentToolPipelineOptions toolPipelineOptions,
        TimeSpan fallbackToolResultTimeout,
        int defaultContextTokens)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(budgetOptions);
        ArgumentNullException.ThrowIfNull(resilienceOptions);
        ArgumentNullException.ThrowIfNull(toolPipelineOptions);

        var timeouts = package.Timeouts;

        var (requestedContext, capacity, reserved) = ResolveContextBudget(package, budgetOptions, defaultContextTokens);

        return new TurnPolicy
        {
            InvocationTimeout = TimeSpan.FromSeconds(timeouts.InvocationTimeoutSeconds),
            StreamIdleTimeout = TimeSpan.FromSeconds(timeouts.StreamIdleTimeoutSeconds),
            StreamIdleTimeoutMessage =
                $"Streaming stalled: no output received for {timeouts.StreamIdleTimeoutSeconds}s (stream idle timeout).",
            ToolResultTimeout = timeouts.ToolCallTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(timeouts.ToolCallTimeoutSeconds)
                : fallbackToolResultTimeout,
            ContextCapacityTokens = capacity,
            RequestedContextTokens = requestedContext,
            ReservedOutputTokens = reserved,
            MaxToolIterationsPerRequest = toolPipelineOptions.MaximumToolIterationsPerRequest,
            MaxConsecutiveInvalidToolCallsPerTool = toolPipelineOptions.MaxConsecutiveInvalidToolCallsPerTool,
            BaseRetryDelay = TimeSpan.FromMilliseconds(resilienceOptions.BaseDelayMilliseconds),
            MaxRetryDelay = TimeSpan.FromMilliseconds(resilienceOptions.MaxDelayMilliseconds),
            CircuitBreakerEnabled = resilienceOptions.CircuitBreakerEnabled,
            CircuitBreakerFailureThreshold = resilienceOptions.CircuitBreakerFailureThreshold,
            CircuitBreakerBreakDuration = TimeSpan.FromSeconds(resilienceOptions.CircuitBreakerBreakDurationSeconds)
        };
    }

    /// <summary>The pre-launch window and output reservation of a package's turn, before <see cref="WithEffectiveContext" /> folds in a launched window.</summary>
    /// <remarks>
    ///     The per-send <c>num_ctx</c> override wins, else the configured default. The benchmark freeze budgets against the same numbers
    ///     (<c>InvocationRunner.BudgetFirstRound</c>), so its pre-flight refusal cannot drift from what <see cref="Resolve" /> sets.
    /// </remarks>
    internal static (int? RequestedContextTokens, int ContextCapacityTokens, int ReservedOutputTokens) ResolveContextBudget(RuntimePackage package,
        ConversationContextBudgetOptions budgetOptions,
        int defaultContextTokens)
    {
        var requestedContext = package.SamplingOptions?.NumCtx is { } numCtx && numCtx > 0 ? numCtx : (int?)null;
        var reserved = ResolveReservedOutputTokens(package.ReservedOutputTokensOverride,
            package.SamplingOptions?.MaxOutputTokens,
            budgetOptions.ReservedOutputTokenFloor);
        return (requestedContext, requestedContext ?? defaultContextTokens, reserved);
    }

    /// <summary>The output tokens held back from the window before the input is measured.</summary>
    /// <remarks>
    ///     An explicit override (the benchmark primary's own max output tokens) is taken exactly; otherwise the floor, widened by
    ///     any explicit max-output-tokens.
    /// </remarks>
    public static int ResolveReservedOutputTokens(int? reservedOutputTokensOverride, int? maxOutputTokens, int floor)
    {
        if (reservedOutputTokensOverride is { } exact && exact >= 0)
        {
            return exact;
        }

        return Math.Max(floor, maxOutputTokens is { } maxOutput && maxOutput > 0 ? maxOutput : 0);
    }

    /// <summary>
    ///     Folds the window the model was ACTUALLY launched with (llama.cpp's <c>-c</c>, read once the model is warm)
    ///     into this policy, so the outer conversation budgeter sizes against the real window.
    /// </summary>
    /// <remarks>
    ///     Precedence: a known effective window plus a per-send <see cref="RequestedContextTokens" /> override takes the smaller of the two, since the user
    ///     asked for a bound but the launched window still caps what is usable; a known window with no override REPLACES the configured default in both
    ///     directions, because clamping instead pins a large-window model to the 8k default; an unknown window leaves the policy unchanged.
    ///     <see cref="ReservedOutputTokens" /> is only ever clamped down, and this stays in lockstep with the inner budgeter's <c>num_ctx</c> resolution.
    /// </remarks>
    public TurnPolicy WithEffectiveContext(int? effectiveContextTokens)
    {
        if (effectiveContextTokens is not > 0)
        {
            return this;
        }

        var effective = effectiveContextTokens.Value;

        return this with
        {
            ContextCapacityTokens = RequestedContextTokens is not null
                ? Math.Min(ContextCapacityTokens, effective)
                : effective,
            ReservedOutputTokens = Math.Min(ReservedOutputTokens, effective)
        };
    }
}
