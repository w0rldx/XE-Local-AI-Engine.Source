namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Operator-tunable knobs for the pre-first-token provider retry and the per-resolved-model circuit breaker around
///     the streaming provider send, bound from <c>Agent:ProviderResilience</c>.
/// </summary>
/// <remarks>
///     Node-level operational settings, NOT part of a runtime package's cross-repo config hash. Defaults are on and
///     conservative, so a fresh install self-heals transient inference blips without an operator opting in.
/// </remarks>
public sealed class ProviderResilienceOptions
{
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

    /// <summary>Whether the circuit breaker (keyed by resolved model) is active.</summary>
    public bool CircuitBreakerEnabled { get; set; } = true;

    /// <summary>
    ///     Number of consecutive transient failures for one resolved model that trips its breaker open. While open,
    ///     sends to that model fail fast with a sanitized "provider temporarily unavailable" error.
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    ///     How long a tripped breaker stays open (fail-fast) before its window elapses and sends are admitted again.
    /// </summary>
    /// <remarks>
    ///     A time-based open window, not a strict single half-open trial: once it elapses every caller is admitted, so
    ///     concurrent probes are possible, and the first admitted send to fail re-opens the breaker for another window
    ///     while the first to succeed closes it. Real concurrency is bounded by the runner's single-invocation guard.
    /// </remarks>
    public int CircuitBreakerBreakDurationSeconds { get; set; } = 30;
}
