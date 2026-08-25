namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Diagnostics;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Per-invocation provider-budget state, flowed implicitly through the whole agent run as an
///     <see cref="AsyncLocal{T}" /> — mirroring how <c>SpawnContext</c> flows the spawn caps. The invocation runner seeds
///     one scope when a turn begins; the innermost pipeline hop (the provider-boundary budget middleware) reads it on
///     EVERY raw provider round — including the inner tool-calling rounds that never surface to the runner and every MAF
///     participant turn — to enforce the cumulative call-count and input-token ceilings. A missing ambient scope means
///     "no budget" (the eval runner and preview-workflow runner drive the same shared client without seeding one), so the
///     middleware degrades to a pass-through.
/// </summary>
public sealed class ProviderCallBudget
{
    /// <summary>
    ///     Fixed, path-free terminal message surfaced when either cumulative ceiling trips. Carries no token counts,
    ///     model names, or content — safe to forward to the caller verbatim.
    /// </summary>
    public const string CeilingExceededMessage =
        "The agent exceeded this turn's provider-call budget (a runaway tool or hand-off loop) and was stopped — start a new chat or simplify the request.";

    /// <summary>
    ///     Fixed, path-free terminal message for the OTHER trip: a caller-seeded per-step cap
    ///     (<see cref="BeginCallCapScope" />) that is TIGHTER than the configured invocation ceiling. That is a bound
    ///     being spent, not a runaway loop — the work-session supervisor ends the step and the next one resumes from the
    ///     saved state — so the copy must not read as a fault. Pinned by
    ///     <c>ProviderCallBudgetTests.RegisterProviderRound_WhenTheStepCapTrips_ReportsTheStepMessage</c> and matched
    ///     verbatim by the supervisor and by the chat pane (<c>ChatMessage.tsx</c>).
    /// </summary>
    public const string StepCallCapReachedMessage =
        "This step reached its provider-call cap; the work session continues from its saved state on the next step.";

    // The single ambient slot. AsyncLocal flows the value into every continuation the run awaits, including the
    // function-invocation pipeline's inner provider rounds and the MAF workflow's participant turns, so the middleware
    // reads the invocation's shared counters without threading a parameter through the MAF/IChatClient surface.
    private static readonly AsyncLocal<ProviderCallBudget?> AmbientBudget = new();

    // A caller-seeded TIGHTENING of MaxProviderCallsPerInvocation, read when a scope is created. The runner builds its
    // own scope from the node options, so an outer caller cannot pass a per-run cap by seeding a budget itself — its
    // scope is immediately replaced. A work-session step is that caller: one step made 14 tool calls on 2026-08-24 and
    // the re-sent results overran the window from inside the loop, which no cross-step bound can reach.
    private static readonly AsyncLocal<int?> AmbientMaxProviderCalls = new();

    private readonly int _maxProviderCalls;

    // True when the ambient per-step cap, not the configured invocation ceiling, is what _maxProviderCalls holds —
    // the one bit that tells a spent step bound apart from a runaway loop when the call count trips.
    private readonly bool _callCapTightened;
    private readonly int _maxCumulativeInputTokens;
    private readonly long _startedTimestamp;
    private long _charsTruncated;
    private int _providerCalls;
    private int _providerRoundsRejected;
    private long _providerRoundElapsedMicroseconds;
    private long _cumulativeInputTokens;
    private long _rejectedInputTokens;
    private long _toolSchemaTokens;
    private int _maximumEstimatedInputTokens;
    private int _maximumToolSchemaTokens;
    private long _messagesDropped;
    private long _toolResultsTruncated;
    private int _toolCallsRequested;
    private int _toolCallsCompleted;
    private int _toolCallsFailed;
    private long _toolRequestToResultMicroseconds;
    private long _toolResultBytes;
    private long _firstToolRequestMicroseconds = -1;
    private int _providerRetries;
    private int _toolArgumentRepairs;
    private int _agentHandoffs;

    private ProviderCallBudget(ProviderCallBudgetOptions options, long startedTimestamp)
    {
        Options = options;
        // "<=" not "<": a step cap seeded at exactly the invocation ceiling is still the caller's per-step bound, and its
        // trip must read as a spent step, not a runaway loop.
        _callCapTightened = AmbientMaxProviderCalls.Value is { } ambient && ambient <= options.MaxProviderCallsPerInvocation;
        _maxProviderCalls = _callCapTightened ? AmbientMaxProviderCalls.Value!.Value : options.MaxProviderCallsPerInvocation;
        _maxCumulativeInputTokens = options.MaxCumulativeInputTokens;
        _startedTimestamp = startedTimestamp;
    }

    /// <summary>The per-round budgeting knobs (context window / reserve / keep-count / excerpt size), shared with the middleware.</summary>
    public ProviderCallBudgetOptions Options { get; }

    /// <summary>The ambient budget for the current async flow, or <see langword="null" /> when none was seeded (no budgeting).</summary>
    public static ProviderCallBudget? Current => AmbientBudget.Value;

    /// <summary>Total raw provider rounds registered so far this invocation.</summary>
    public int ProviderCalls => Volatile.Read(ref _providerCalls);

    /// <summary>Total estimated input tokens registered so far this invocation.</summary>
    public long CumulativeInputTokens => Interlocked.Read(ref _cumulativeInputTokens);

    /// <summary>
    ///     Seeds a fresh budget scope for the current async flow and returns a disposable that restores the prior ambient
    ///     value on dispose (so a nested seed cannot leak counters into an outer turn). Called once when a root invocation
    ///     begins.
    /// </summary>
    public static IDisposable BeginScope(ProviderCallBudgetOptions options)
    {
        return BeginScope(options, Stopwatch.GetTimestamp());
    }

    /// <summary>
    ///     Seeds a scope whose latency baselines start at a caller-owned timestamp. The production invocation runner
    ///     passes its turn-start timestamp so time-to-first-tool includes admission/context work before the budget scope
    ///     itself is created; direct/test callers use <see cref="BeginScope(ProviderCallBudgetOptions)" />.
    /// </summary>
    internal static IDisposable BeginScope(ProviderCallBudgetOptions options, long startedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(options);

        var previous = AmbientBudget.Value;
        AmbientBudget.Value = new ProviderCallBudget(options, startedTimestamp);
        return new Scope(previous);
    }

    /// <summary>
    ///     Tightens <see cref="ProviderCallBudgetOptions.MaxProviderCallsPerInvocation" /> for every scope created in the
    ///     current async flow, and returns a disposable restoring the prior value. Seed it BEFORE the run begins — the
    ///     runner creates its own scope from the node options, so this is the only way an outer caller can cap one run.
    ///     <b>Tighten-only</b>: a value at or above the configured ceiling is ignored, so no caller can raise it.
    /// </summary>
    public static IDisposable BeginCallCapScope(int maxProviderCalls)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProviderCalls);

        var previous = AmbientMaxProviderCalls.Value;
        AmbientMaxProviderCalls.Value = maxProviderCalls;
        return new CallCapScope(previous);
    }

    /// <summary>
    ///     Registers one raw provider round of <paramref name="estimatedInputTokens" /> against the cumulative ceilings.
    ///     Throws <see cref="ProviderCallBudgetExceededException" /> when either ceiling is exceeded, so the middleware can
    ///     fail the round BEFORE calling the provider rather than after. Atomic increments make it safe across the
    ///     concurrent participant runs an orchestration may drive.
    /// </summary>
    public void RegisterProviderRound(int estimatedInputTokens,
        int toolSchemaTokens = 0,
        int messagesDropped = 0,
        int toolResultsTruncated = 0,
        int charsTruncated = 0)
    {
        var normalizedInputTokens = Math.Max(0, estimatedInputTokens);
        var calls = Interlocked.Increment(ref _providerCalls);
        var tokens = Interlocked.Add(ref _cumulativeInputTokens, normalizedInputTokens);

        if (calls > _maxProviderCalls || tokens > _maxCumulativeInputTokens)
        {
            Interlocked.Increment(ref _providerRoundsRejected);
            Interlocked.Add(ref _rejectedInputTokens, normalizedInputTokens);

            // Only a call-count trip under a caller-tightened step cap is the benign "bound spent" case. A cumulative
            // input-token trip keeps the runaway wording even under a step cap — that one is never routine.
            var stepCapReached = _callCapTightened && tokens <= _maxCumulativeInputTokens;
            throw new ProviderCallBudgetExceededException(stepCapReached ? StepCallCapReachedMessage : CeilingExceededMessage);
        }

        Interlocked.Add(ref _toolSchemaTokens, Math.Max(0, toolSchemaTokens));
        Interlocked.Add(ref _messagesDropped, Math.Max(0, messagesDropped));
        Interlocked.Add(ref _toolResultsTruncated, Math.Max(0, toolResultsTruncated));
        Interlocked.Add(ref _charsTruncated, Math.Max(0, charsTruncated));
        UpdateMaximum(ref _maximumEstimatedInputTokens, normalizedInputTokens);
        UpdateMaximum(ref _maximumToolSchemaTokens, Math.Max(0, toolSchemaTokens));
    }

    internal void RecordProviderRoundElapsed(TimeSpan duration)
    {
        Interlocked.Add(ref _providerRoundElapsedMicroseconds, ToMicroseconds(duration));
    }

    internal void RecordToolCallRequested()
    {
        Interlocked.Increment(ref _toolCallsRequested);
        var elapsedMicroseconds = ToMicroseconds(Stopwatch.GetElapsedTime(_startedTimestamp));
        Interlocked.CompareExchange(ref _firstToolRequestMicroseconds, elapsedMicroseconds, comparand: -1);
    }

    internal void RecordToolCallCompleted(TimeSpan requestToResultLatency, int resultBytes, bool failed)
    {
        Interlocked.Increment(ref _toolCallsCompleted);
        if (failed)
        {
            Interlocked.Increment(ref _toolCallsFailed);
        }

        Interlocked.Add(ref _toolRequestToResultMicroseconds, ToMicroseconds(requestToResultLatency));
        Interlocked.Add(ref _toolResultBytes, Math.Max(0, resultBytes));
    }

    internal void RecordProviderRetry()
    {
        Interlocked.Increment(ref _providerRetries);
    }

    internal void RecordToolArgumentRepair()
    {
        Interlocked.Increment(ref _toolArgumentRepairs);
    }

    internal void RecordAgentHandoff()
    {
        Interlocked.Increment(ref _agentHandoffs);
    }

    internal ProviderCallEfficiencySnapshot CaptureEfficiencySnapshot()
    {
        var attempts = Volatile.Read(ref _providerCalls);
        var rejected = Volatile.Read(ref _providerRoundsRejected);
        var firstToolRequestMicroseconds = Interlocked.Read(ref _firstToolRequestMicroseconds);

        return new ProviderCallEfficiencySnapshot(ProviderCalls: Math.Max(0, attempts - rejected),
            ProviderRoundsRejected: rejected,
            EstimatedInputTokens: Math.Max(0, Interlocked.Read(ref _cumulativeInputTokens) - Interlocked.Read(ref _rejectedInputTokens)),
            MaximumEstimatedInputTokens: Volatile.Read(ref _maximumEstimatedInputTokens),
            ToolSchemaTokens: Interlocked.Read(ref _toolSchemaTokens),
            MaximumToolSchemaTokens: Volatile.Read(ref _maximumToolSchemaTokens),
            ProviderRoundElapsedMs: FromMicroseconds(Interlocked.Read(ref _providerRoundElapsedMicroseconds)),
            MessagesDropped: Interlocked.Read(ref _messagesDropped),
            ToolResultsTruncated: Interlocked.Read(ref _toolResultsTruncated),
            CharsTruncated: Interlocked.Read(ref _charsTruncated),
            ToolCallsRequested: Volatile.Read(ref _toolCallsRequested),
            ToolCallsCompleted: Volatile.Read(ref _toolCallsCompleted),
            ToolCallsFailed: Volatile.Read(ref _toolCallsFailed),
            ToolRequestToResultMs: FromMicroseconds(Interlocked.Read(ref _toolRequestToResultMicroseconds)),
            ToolResultBytes: Interlocked.Read(ref _toolResultBytes),
            TimeToFirstToolRequestMs: firstToolRequestMicroseconds < 0 ? null : FromMicroseconds(firstToolRequestMicroseconds),
            ProviderRetries: Volatile.Read(ref _providerRetries),
            ToolArgumentRepairs: Volatile.Read(ref _toolArgumentRepairs),
            AgentHandoffs: Volatile.Read(ref _agentHandoffs));
    }

    private static long ToMicroseconds(TimeSpan duration)
    {
        return duration <= TimeSpan.Zero ? 0 : (long)Math.Min(long.MaxValue, duration.TotalMicroseconds);
    }

    private static double FromMicroseconds(long microseconds)
    {
        return Math.Max(0, microseconds) / 1000d;
    }

    private static void UpdateMaximum(ref int location, int candidate)
    {
        var observed = Volatile.Read(ref location);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref location, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly ProviderCallBudget? _previous;

        public Scope(ProviderCallBudget? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientBudget.Value = _previous;
        }
    }

    // Restores the prior ambient call cap when disposed, so a nested seed cannot leak into an outer turn.
    private sealed class CallCapScope : IDisposable
    {
        private readonly int? _previous;

        public CallCapScope(int? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientMaxProviderCalls.Value = _previous;
        }
    }
}

/// <summary>
///     Immutable, content-free aggregate of the expensive work performed during one root agent invocation. It contains
///     counts, durations, and estimated sizes only — never prompts, model output, tool identities, arguments, results,
///     paths, or schemas — so the invocation runner can export it safely through bounded telemetry.
/// </summary>
internal sealed record ProviderCallEfficiencySnapshot(
    int ProviderCalls,
    int ProviderRoundsRejected,
    long EstimatedInputTokens,
    int MaximumEstimatedInputTokens,
    long ToolSchemaTokens,
    int MaximumToolSchemaTokens,
    double ProviderRoundElapsedMs,
    long MessagesDropped,
    long ToolResultsTruncated,
    long CharsTruncated,
    int ToolCallsRequested,
    int ToolCallsCompleted,
    int ToolCallsFailed,
    double ToolRequestToResultMs,
    long ToolResultBytes,
    double? TimeToFirstToolRequestMs,
    int ProviderRetries,
    int ToolArgumentRepairs,
    int AgentHandoffs);
