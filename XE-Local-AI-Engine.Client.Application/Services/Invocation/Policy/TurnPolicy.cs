namespace XE_Local_AI_Engine.Client.Services.Invocation.Policy;

using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Immutable, per-turn snapshot of every timeout/retry/budget knob that governs one invocation. Resolved ONCE in
///     <c>InvocationRunner.RunAsync</c> and flowed unchanged through both the single-agent and orchestration paths so
///     the two enforce identical policy for one turn. This is a RESOLUTION + DOCUMENTATION seam only: every field is
///     copied from an existing configured source (the package's <see cref="TimeoutSettings" />, and the
///     <c>Agent:ConversationContextBudget</c>, <c>Agent:ProviderResilience</c>, and <c>Agent:ToolPipeline</c>
///     configuration sections) — none of those keys move, rename, or change shape, and nothing here is itself
///     persisted or part of a runtime package's config hash.
///     <para>
///         Composite turn budget — the order in which a stalled or slow turn trips a bound, and who owns each:
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <see cref="StreamIdleTimeout" /> (package <c>TimeoutSettings.StreamIdleTimeoutSeconds</c>, enforced
///                 by <c>StreamIdleWatchdog</c>): no chunk arrives between two yielded items of ONE streamed segment —
///                 the tightest, most local bound. The orchestration path enforces an analogous per-quiescence idle
///                 bound in <c>OrchestrationRunSession</c>, but that timer is sourced from the separate node-global
///                 <c>OrchestrationAgentOptions.IdleTimeoutSeconds</c>, NOT from this package field — a pre-existing
///                 split this policy documents rather than silently unifies (unifying it is a behavior change out of
///                 scope for this consolidation).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="ToolResultTimeout" /> (package <c>TimeoutSettings.ToolCallTimeoutSeconds</c> when set,
///                 else the node-global pending-tool-call age): bounds the wait for an API-side tool call's RESULT to
///                 come back over the hub round-trip. The separate human-APPROVAL wait always uses the node-global
///                 pending-tool-call age, deliberately never this shorter per-tool budget.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="InvocationTimeout" /> (package <c>TimeoutSettings.InvocationTimeoutSeconds</c>): the
///                 whole turn's wall-clock ceiling, covering every segment/approval round-trip end to end.
///             </description>
///         </item>
///     </list>
///     <para>
///         Context budgeting (<see cref="ContextCapacityTokens" />/<see cref="ReservedOutputTokens" />) is orthogonal
///         to the three timeouts above: it bounds what history is SENT to the provider, not how long the provider is
///         given to answer it. <see cref="RetryEnabled" />/<see cref="MaxRetries" />/<see cref="CircuitBreakerEnabled" />
///         govern only the pre-first-token send of the FIRST segment (see <c>ProviderStreamResilience</c>);
///         <see cref="MaxToolIterationsPerRequest" />/<see cref="MaxConsecutiveInvalidToolCallsPerTool" /> are the
///         node-global tool-pipeline ceilings applied by the DI-wired <c>FunctionInvokingChatClient</c> and
///         <c>ToolArgumentRepairAIFunction</c> respectively — copied here as read-only reference values (this record
///         does not itself enforce them) so one place documents the whole turn's bounds.
///     </para>
/// </summary>
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
    ///     <see langword="null" /> when the capacity is the configured <see cref="ConversationContextBudgetOptions.DefaultContextTokens" />
    ///     fallback. <see cref="WithEffectiveContext" /> needs the distinction: a user-requested bound is a ceiling to
    ///     keep, whereas the untrusted default must be REPLACED by the window the model actually launched with.
    /// </summary>
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
    ///     node-level operational options. <paramref name="fallbackToolResultTimeout" /> is the node-global pending
    ///     tool-call age used when the package sets no explicit <c>ToolCallTimeoutSeconds</c> (mirrors the
    ///     pre-existing <c>ResolveConfiguredToolResultTimeout</c> fallback).
    /// </summary>
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
    ///     into this policy, so the outer conversation budgeter sizes against the real window. Precedence:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 known effective window + a per-send <see cref="RequestedContextTokens" /> override → the smaller
    ///                 of the two (the user asked for a bound, but the launched window still caps what is usable);
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 known effective window, no override → the effective window REPLACES the configured default in
    ///                 both directions. Clamping instead (the pre-fix behavior) pinned every large-window model to the
    ///                 8k default and failed long conversations with <c>ContextBudgetExceededException</c> while the
    ///                 process happily ran a 64k window;
    ///             </description>
    ///         </item>
    ///         <item><description>unknown effective window → unchanged (override, else the configured default).</description></item>
    ///     </list>
    ///     Kept in lockstep with <c>InvocationAgentFactory</c>, which resolves the same precedence into the
    ///     <c>num_ctx</c> chat option the INNER per-round budgeter reads — the two must agree for a warm local model.
    ///     <see cref="ReservedOutputTokens" /> is only ever clamped down: it can never exceed the real window.
    /// </summary>
    /// <summary>
    ///     Widens the turn's reserved output budget to the one the reasoning-effort dispatcher chose for an
    ///     <c>auto</c> turn. Mirrors <see cref="WithEffectiveContext" />'s shape: a null (or non-positive) value
    ///     returns <c>this</c>, so every non-<c>auto</c> turn is a reference-identical no-op.
    ///     <para>
    ///         It only ever widens. <see cref="WithEffectiveContext" /> runs AFTER it and clamps the reservation down
    ///         to the window the model was actually launched with, so the real window always has the last word.
    ///     </para>
    /// </summary>
    public TurnPolicy WithDispatchedOutputBudget(int? dispatchedOutputTokens)
    {
        if (dispatchedOutputTokens is not > 0)
        {
            return this;
        }

        return this with { ReservedOutputTokens = Math.Max(ReservedOutputTokens, dispatchedOutputTokens.Value) };
    }

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
