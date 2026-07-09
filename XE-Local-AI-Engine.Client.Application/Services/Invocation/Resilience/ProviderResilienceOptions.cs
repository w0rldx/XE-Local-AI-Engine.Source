namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Operator-tunable knobs for the pre-first-token provider retry and the per-endpoint circuit breaker applied
///     around the streaming provider send in the agent invocation path. These are node-level operational resilience
///     settings (NOT part of a runtime package's cross-repo config hash), bound from the
///     <c>Agent:ProviderResilience</c> configuration section. Defaults are on and conservative so a fresh install
///     self-heals transient inference blips without an operator having to opt in.
/// </summary>
public sealed class ProviderResilienceOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Agent:ProviderResilience";

    /// <summary>
    ///     Whether the bounded retry runs at all. When false the provider send is attempted exactly once and any
    ///     failure is surfaced immediately (the pre-hardening behaviour).
    /// </summary>
    public bool RetryEnabled { get; set; } = true;

    /// <summary>
    ///     Maximum number of RETRIES (in addition to the first attempt) for a transient failure that occurs before the
    ///     first streamed chunk. A value of 2 means up to three total attempts.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base backoff delay in milliseconds; the exponential schedule starts here.</summary>
    public int BaseDelayMilliseconds { get; set; } = 500;

    /// <summary>Upper bound (before jitter) on any single backoff delay in milliseconds.</summary>
    public int MaxDelayMilliseconds { get; set; } = 2000;

    /// <summary>Whether the per-endpoint circuit breaker is active.</summary>
    public bool CircuitBreakerEnabled { get; set; } = true;

    /// <summary>
    ///     Number of consecutive transient failures on one endpoint that trips its breaker open. While open, sends to
    ///     that endpoint fail fast with a sanitized "provider temporarily unavailable" error.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>How long a tripped breaker stays open before allowing a single half-open trial send.</summary>
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;
}
