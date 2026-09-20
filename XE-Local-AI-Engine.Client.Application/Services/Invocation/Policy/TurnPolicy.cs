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
    ///     whereas the untrusted <see cref="ConversationContextBudgetOptions.DefaultContextTokens" /> fallback must be
    ///     REPLACED by the window the model actually launched with.
    /// </remarks>
    public int? RequestedContextTokens { get; init; }

    public required int ReservedOutputTokens { get; init; }

    public required int MaxToolIterationsPerRequest { get; init; }

    public required int MaxConsecutiveInvalidToolCallsPerTool { get; init; }

    public required bool RetryEnabled { get; init; }

    public required int MaxRetries { get; init; }

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
    public static TurnPolicy Resolve(RuntimePackage package,
        ConversationContextBudgetOptions budgetOptions,
        ProviderResilienceOptions resilienceOptions,
        AgentToolPipelineOptions toolPipelineOptions,
        TimeSpan fallbackToolResultTimeout)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(budgetOptions);
        ArgumentNullException.ThrowIfNull(resilienceOptions);
        ArgumentNullException.ThrowIfNull(toolPipelineOptions);

        var timeouts = package.Timeouts;

        // Mirrors the pre-existing InvocationRunner.ResolveContextBudget: the per-send num_ctx override wins, else the
        // configured default; the reserved-output floor is widened by any explicit max-output-tokens override.
        var requestedContext = package.SamplingOptions?.NumCtx is { } numCtx && numCtx > 0 ? numCtx : (int?)null;
        var capacity = requestedContext ?? budgetOptions.DefaultContextTokens;
        var requestedOutput = package.SamplingOptions?.MaxOutputTokens is { } maxOutput && maxOutput > 0
            ? maxOutput
            : 0;
        var reserved = Math.Max(budgetOptions.ReservedOutputTokenFloor, requestedOutput);

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
            RetryEnabled = resilienceOptions.RetryEnabled,
            MaxRetries = resilienceOptions.MaxRetries,
            BaseRetryDelay = TimeSpan.FromMilliseconds(resilienceOptions.BaseDelayMilliseconds),
            MaxRetryDelay = TimeSpan.FromMilliseconds(resilienceOptions.MaxDelayMilliseconds),
            CircuitBreakerEnabled = resilienceOptions.CircuitBreakerEnabled,
            CircuitBreakerFailureThreshold = resilienceOptions.CircuitBreakerFailureThreshold,
            CircuitBreakerBreakDuration = TimeSpan.FromSeconds(resilienceOptions.CircuitBreakerBreakDurationSeconds)
        };
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
