namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     How long a benchmark phase waits for local capacity before it gives up. A capacity rejection is transient by
///     nature — it means something holds the bytes right now — and the primary and judge phases share ONE FIFO
///     consumer, so a judge dequeued while the preceding primary's llama-server is still releasing its VRAM is the
///     normal handoff, not an error. Waiting turns that into a delay instead of a terminal failure the operator has to
///     re-judge by hand.
/// </summary>
/// <param name="MaxRetries">Re-decisions after the first rejection. Total decisions = <c>MaxRetries + 1</c>.</param>
/// <param name="Interval">Delay between decisions. A fresh decision re-probes free VRAM, so the wait is not a spin.</param>
public sealed record BenchmarkAdmissionRetry(int MaxRetries, TimeSpan Interval)
{
    /// <summary>24 retries × 5 s ⇒ up to two minutes, which covers a large model's VRAM release with room to spare.</summary>
    public static BenchmarkAdmissionRetry Default { get; } = new(MaxRetries: 24, TimeSpan.FromSeconds(5));

    /// <summary>The wall-clock wait the caller is told about when the budget is exhausted.</summary>
    public TimeSpan Budget => MaxRetries * Interval;
}

/// <summary>The per-call identity carried into the admission log line and the caller-facing failure message.</summary>
internal sealed record BenchmarkAdmissionContext(Guid RunId,
    string Phase,
    int RequestedContextTokens,
    string KvCacheType,
    string RejectedMessage);

/// <summary>The one admission path both benchmark executors take, so the wait and the log line cannot diverge.</summary>
internal static class BenchmarkCapacityAdmission
{
    /// <summary>
    ///     Decides capacity, retrying a <see cref="CapacityVerdict.RejectInsufficient" /> on
    ///     <paramref name="retry" />'s cadence. Returns the first non-rejecting decision — whose reservation the caller
    ///     still owns and must dispose — or throws <see cref="BenchmarkExecutionException" /> once the budget is spent.
    /// </summary>
    public static async Task<CapacityDecision> AdmitAsync(ICapacityService capacity,
        CapacityRequest request,
        BenchmarkAdmissionContext context,
        BenchmarkAdmissionRetry retry,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(retry);
        ArgumentNullException.ThrowIfNull(logger);

        for (var attempt = 0; attempt <= retry.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // A fresh decision re-probes live free VRAM under the admission gate, so the wait is a re-measurement,
                // not a spin on a stale reading.
                await Task.Delay(retry.Interval, ct).ConfigureAwait(false);
            }

            var decision = await capacity.DecideAsync(request, ct).ConfigureAwait(false);
            logger.LogInformation("Benchmark capacity admission: run {RunId} phase {Phase} model {ModelName}, requested context {RequestedContextTokens}, "
                                  + "frozen runtime context {FrozenContextTokens}, KV cache {KvCacheType}, attempt {Attempt} of {TotalAttempts} -> {Verdict} ({Reason}).",
                context.RunId,
                context.Phase,
                request.ModelName,
                context.RequestedContextTokens,
                request.RequiredContextTokens,
                context.KvCacheType,
                attempt + 1,
                retry.MaxRetries + 1,
                decision.Verdict,
                decision.Reason);
            if (decision.Verdict != CapacityVerdict.RejectInsufficient)
            {
                return decision;
            }
        }

        throw new BenchmarkExecutionException(
            $"{context.RejectedMessage} No capacity became free after {retry.Budget.TotalSeconds:0} s.");
    }
}
