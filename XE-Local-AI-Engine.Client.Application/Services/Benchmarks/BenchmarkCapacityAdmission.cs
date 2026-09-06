namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     How long a benchmark phase waits for local capacity before it gives up. A capacity rejection is transient by
///     nature — it means something holds the bytes right now — and the primary and judge phases share ONE FIFO
///     consumer, so a judge dequeued while the preceding primary's llama-server is still releasing its VRAM is the
///     normal handoff, not an error. Waiting turns that into a delay instead of a terminal failure the operator has to
///     re-judge by hand.
/// </summary>
/// <remarks>
///     This is the budget for a whole PHASE, not for one wait. A phase can wait twice — first for capacity, then for
///     the exclusive spawn — and both waits sit on the queue's single shared GPU-work admission, so two independent
///     budgets would let one phase hold that admission for twice the configured maximum. A phase therefore takes one
///     <see cref="BenchmarkWaitBudget" /> before its admission and both waits draw their retries from it.
/// </remarks>
/// <param name="MaxRetries">Re-decisions after the first rejection. Total decisions = <c>MaxRetries + 1</c>.</param>
/// <param name="Interval">Delay between decisions. A fresh decision re-probes free VRAM, so the wait is not a spin.</param>
public sealed record BenchmarkAdmissionRetry(int MaxRetries, TimeSpan Interval)
{
    /// <summary>24 retries × 5 s ⇒ up to two minutes, which covers a large model's VRAM release with room to spare.</summary>
    public static BenchmarkAdmissionRetry Default { get; } = new(MaxRetries: 24, TimeSpan.FromSeconds(5));

    /// <summary>The wall-clock wait the caller is told about when the budget is exhausted.</summary>
    public TimeSpan Budget => MaxRetries * Interval;
}

/// <summary>
///     One phase's share of <see cref="BenchmarkAdmissionRetry" />, drawn down by every wait that phase makes. Taken
///     once, before admission, and handed to both waits so their retries come out of the same allowance instead of
///     each getting a full one.
/// </summary>
internal sealed class BenchmarkWaitBudget(BenchmarkAdmissionRetry retry)
{
    private int _used;

    /// <summary>Delay before each re-decision.</summary>
    public TimeSpan Interval { get; } = retry.Interval;

    /// <summary>The phase's whole wall-clock maximum — what a spent budget reports, not what is left of it.</summary>
    public TimeSpan Budget { get; } = retry.Budget;

    /// <summary>Retries this phase still has, across all of its waits.</summary>
    public int Remaining => retry.MaxRetries - _used;

    /// <summary>Takes one retry from the phase's share. Called immediately before each wait interval.</summary>
    public void Consume() =>
        _used++;
}

/// <summary>The per-call identity carried into the admission log line and the caller-facing failure message.</summary>
internal sealed record BenchmarkAdmissionContext(
    Guid RunId,
    string Phase,
    int RequestedContextTokens,
    string KvCacheType,
    string RejectedMessage);

/// <summary>The one admission path both benchmark executors take, so the wait and the log line cannot diverge.</summary>
internal static class BenchmarkCapacityAdmission
{
    /// <summary>
    ///     Decides capacity, retrying a <see cref="CapacityVerdict.RejectInsufficient" /> on <paramref name="budget" />'s
    ///     cadence and out of <paramref name="budget" />'s remaining share. Returns the first non-rejecting decision —
    ///     whose reservation the caller still owns and must dispose — or throws
    ///     <see cref="BenchmarkExecutionException" /> once the phase's budget is spent.
    /// </summary>
    public static async Task<CapacityDecision> AdmitAsync(ICapacityService capacity,
        CapacityRequest request,
        BenchmarkAdmissionContext context,
        BenchmarkWaitBudget budget,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(logger);

        var attempt = 0;
        while (true)
        {
            if (attempt > 0)
            {
                // A fresh decision re-probes live free VRAM under the admission gate, so the wait is a re-measurement,
                // not a spin on a stale reading.
                budget.Consume();
                await Task.Delay(budget.Interval, ct).ConfigureAwait(false);
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
                attempt + 1 + budget.Remaining,
                decision.Verdict,
                decision.Reason);
            if (decision.Verdict != CapacityVerdict.RejectInsufficient)
            {
                return decision;
            }

            if (budget.Remaining == 0)
            {
                throw new BenchmarkExecutionException($"{context.RejectedMessage} No capacity became free after {budget.Budget.TotalSeconds:0} s.");
            }

            attempt++;
        }
    }
}

/// <summary>
///     The one path every queued benchmark phase takes to the supervisor's exclusive profiling spawn.
///     <para>
///         A <see cref="LlamaServerProfilingRefusedException" /> is the SAME shape of blocker a capacity rejection is:
///         a warm role for the model is serving a request right now, and the request ends on its own. Left to reach an
///         executor's generic catch it terminalizes durable queued work as failed — with the generic
///         invocation-failed message, since the executors do not translate this type — over a chat that was about to
///         finish, and a primary/judge/comparison item has no second attempt behind that promise. So it is waited out
///         instead, and only a spent budget is terminal, naming the model and role that held it.
///     </para>
///     <para>
///         Retrying repeats no work: the refusal is raised by the pre-spawn eviction, before anything is spawned and
///         before the body runs, and it evicts nothing when it refuses. This wait is the more expensive of a phase's
///         two, and deliberately so: it holds the queue's shared GPU-work admission and the model lease like the
///         capacity wait does, and ALSO the capacity reservation, which is taken only once admission has succeeded.
///         That is why it does not get its own budget — it draws from the phase's remaining share, so the two waits
///         together can never exceed one <see cref="BenchmarkAdmissionRetry" /> allowance.
///     </para>
/// </summary>
internal static class BenchmarkExclusiveSpawn
{
    /// <summary>
    ///     Runs <paramref name="spawn" />, retrying a profiling refusal on <paramref name="budget" />'s cadence and out
    ///     of its remaining share. Throws <see cref="BenchmarkExecutionException" /> once that share is spent; every
    ///     other failure propagates unchanged on the first attempt.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> spawn,
        BenchmarkWaitBudget budget,
        Guid runId,
        string phase,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spawn);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(logger);

        var attempt = 0;
        while (true)
        {
            if (attempt > 0)
            {
                // The refusal is a point-in-time read of the model's leases, so the wait is a re-measurement of who is
                // using it, not a spin on a stale answer.
                budget.Consume();
                await Task.Delay(budget.Interval, ct).ConfigureAwait(false);
            }

            try
            {
                return await spawn(ct).ConfigureAwait(false);
            }
            catch (LlamaServerProfilingRefusedException refusal)
            {
                logger.LogInformation("Benchmark exclusive spawn deferred: run {RunId} phase {Phase}, attempt {Attempt} of {TotalAttempts} -> {Reason}",
                    runId, phase, attempt + 1, attempt + 1 + budget.Remaining, refusal.Message);
                if (budget.Remaining == 0)
                {
                    // The refusal's own sentence is the SKIP wording ("Retry when the model is idle"), which reads as
                    // advice on a row that is now terminal. The typed fields say the same thing as an outcome.
                    throw new BenchmarkExecutionException($"{refusal.ModelName} ({refusal.Role}) was still in use after {budget.Budget.TotalSeconds:0} s; the benchmark did not run.");
                }
            }

            attempt++;
        }
    }
}
