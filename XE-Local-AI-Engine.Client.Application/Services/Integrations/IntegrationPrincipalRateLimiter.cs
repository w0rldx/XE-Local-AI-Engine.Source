namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Threading.RateLimiting;

/// <summary>
///     The per-principal request ceiling for the external integration API, consulted INSIDE the hand-mapped handlers
///     after authentication.
/// </summary>
/// <remarks>
///     The route-level policy cannot do this job: <c>UseRateLimiter</c> runs before <c>UseAuthentication</c>, so at
///     partition time there is no principal and every caller shares one bucket. That layer is a coarse per-IP abuse
///     ceiling (<c>IntegrationOptions.IpRateLimitPerMinute</c>, 6,000/min) and this one is where fairness lives: one
///     integrator exhausting its window must not refuse another (ruling R5-5). Register it as a singleton THROUGH A
///     FACTORY so the container disposes it — the wrapped limiter's replenishment timer otherwise roots the host graph.
/// </remarks>
public sealed class IntegrationPrincipalRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public IntegrationPrincipalRateLimiter(int permitLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);

        _limiter = PartitionedRateLimiter.Create<string, string>(principalId =>
            RateLimitPartition.GetFixedWindowLimiter(principalId,
                _ => new FixedWindowRateLimiterOptions
                {
                    AutoReplenishment = true,
                    PermitLimit = permitLimit,

                    // Never queue. The caller is a synchronous handler on an inference-bound surface: making a request
                    // wait for a permit converts a fast 429 into a slow one and holds a connection for it.
                    QueueLimit = 0,
                    Window = TimeSpan.FromMinutes(1)
                }));
    }

    /// <summary>
    ///     Spends one permit for <paramref name="principalId" />. Returns <see langword="false" /> when that
    ///     principal's window is exhausted; the caller answers 429 with <c>Retry-After: 60</c>, the same convention the
    ///     middleware's own rejection uses.
    /// </summary>
    public bool TryAcquire(string principalId)
    {
        ArgumentException.ThrowIfNullOrEmpty(principalId);

        // AttemptAcquire is the synchronous, non-queueing acquire, which QueueLimit 0 makes the only correct call.
        // Disposing a FIXED-WINDOW lease does not return the permit, so `using` is right here.
        using var lease = _limiter.AttemptAcquire(principalId);
        return lease.IsAcquired;
    }

    public void Dispose() =>
        _limiter.Dispose();
}
