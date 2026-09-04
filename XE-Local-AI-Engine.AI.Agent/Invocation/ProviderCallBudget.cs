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
    //
    // The slot holds the caller's scope OBJECT rather than a bare int, which is what makes the consumption readable
    // back on the caller's side. An AsyncLocal write inside the run never propagates OUT to the caller, so
    // `Current` is null again by the time the caller's await returns; a mutable object the caller seeded BEFORE the
    // run is shared by reference, so the budget the run creates can register itself into it and the caller can read
    // the counters afterwards without any new plumbing through the send path.
    private static readonly AsyncLocal<ProviderCallCapScope?> AmbientCallCap = new();

    /// <summary>
    ///     How many DISTINCT tool names one budget keeps — and, deliberately, the ONE number anything downstream caps
    ///     to as well. Bounded here rather than at a reader, so a runaway tool loop cannot grow the set: the
    ///     seventeenth distinct name is dropped at the source, not on the way out.
    /// </summary>
    internal const int MaxDistinctToolNames = 16;

    /// <summary>
    ///     How long ONE recorded tool name may be, marker included. Bounded at the source for the same reason the
    ///     distinct-name count is: the count caps how MANY names a carrier holds, and only this caps how BIG each of
    ///     them is. Without it a single oversized identifier reaches the persisted step detail, and from there the
    ///     work-session event detail, unbounded — the node-run column's own 1024-character clamp is applied later and
    ///     only on that one carrier.
    /// </summary>
    internal const int MaxToolNameLength = 128;

    /// <summary>The last characters of a clamped name. Unmistakably not part of any real tool identifier.</summary>
    internal const string TruncatedToolNameMarker = "…";

    private readonly int _maxProviderCalls;

    // Names only, ordinal-sorted, bounded. The counters beside it say how MUCH a run spent; this says WHICH tools it
    // reached for — a fixed id for a built-in and an operator-authored identifier for an MCP or custom tool, never an
    // argument and never a result.
    private readonly Lock _toolNameGate = new();
    private readonly SortedSet<string> _toolNames = new(StringComparer.Ordinal);

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
        _callCapTightened = AmbientCallCap.Value is { } ambient && ambient.MaxProviderCalls <= options.MaxProviderCallsPerInvocation;
        _maxProviderCalls = _callCapTightened ? AmbientCallCap.Value!.MaxProviderCalls : options.MaxProviderCallsPerInvocation;
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
    ///     The distinct tool names this invocation asked for, ordinal-sorted and capped at sixteen. A snapshot: the set
    ///     keeps moving while the run does.
    /// </summary>
    internal IReadOnlyList<string> ToolNames
    {
        get
        {
            lock (_toolNameGate)
            {
                return [.. _toolNames];
            }
        }
    }

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
        var budget = new ProviderCallBudget(options, startedTimestamp);
        AmbientBudget.Value = budget;
        // Registered AFTER construction rather than from the constructor, so a half-built instance is never published
        // to a caller that could read it from another thread.
        AmbientCallCap.Value?.Attach(budget);
        return new Scope(previous);
    }

    /// <summary>
    ///     Tightens <see cref="ProviderCallBudgetOptions.MaxProviderCallsPerInvocation" /> for every scope created in the
    ///     current async flow, and returns a disposable restoring the prior value. Seed it BEFORE the run begins — the
    ///     runner creates its own scope from the node options, so this is the only way an outer caller can cap one run.
    ///     <b>Tighten-only</b>: a value at or above the configured ceiling is ignored, so no caller can raise it.
    ///     <para>
    ///         The returned handle is also how the caller MEASURES what the run spent: every budget created inside the
    ///         scope registers itself, so <see cref="ProviderCallCapScope.CaptureConsumption" /> answers with the
    ///         content-free counts once the run has landed. That is the only readable seam — the run's own ambient
    ///         budget is invisible from out here, because an <see cref="AsyncLocal{T}" /> written inside the run does
    ///         not flow back to its caller.
    ///     </para>
    /// </summary>
    public static ProviderCallCapScope BeginCallCapScope(int maxProviderCalls)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProviderCalls);

        var scope = new ProviderCallCapScope(maxProviderCalls, AmbientCallCap.Value);
        AmbientCallCap.Value = scope;
        return scope;
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

    /// <param name="toolName">
    ///     The tool the model asked for, kept as a bounded set of distinct NAMES beside the count. The name is what
    ///     turns "this step made nine tool calls" into "and they were these four tools", which is the question a cost
    ///     rollup actually gets asked. Null or blank records the count alone — which is what the caller passes for a
    ///     name that did NOT resolve against the tools the request offered, because a hallucinated identifier is a call
    ///     the model attempted, not a tool this run reached for.
    ///     <para>
    ///         A resolved name is clamped to <see cref="MaxToolNameLength" /> HERE rather than at a reader, so every
    ///         carrier downstream — the persisted step detail, the work-session event detail, the node-run column — is
    ///         bounded by construction rather than by whichever of them happens to clamp.
    ///     </para>
    /// </param>
    internal void RecordToolCallRequested(string? toolName)
    {
        Interlocked.Increment(ref _toolCallsRequested);
        var elapsedMicroseconds = ToMicroseconds(Stopwatch.GetElapsedTime(_startedTimestamp));
        Interlocked.CompareExchange(ref _firstToolRequestMicroseconds, elapsedMicroseconds, comparand: -1);

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return;
        }

        var bounded = toolName.Length <= MaxToolNameLength
            ? toolName
            : string.Concat(toolName.AsSpan(0, MaxToolNameLength - TruncatedToolNameMarker.Length), TruncatedToolNameMarker);

        lock (_toolNameGate)
        {
            if (_toolNames.Count < MaxDistinctToolNames)
            {
                _ = _toolNames.Add(bounded);
            }
        }
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

    /// <summary>Restores the ambient call cap a <see cref="ProviderCallCapScope" /> replaced, on its dispose.</summary>
    internal static void RestoreCallCap(ProviderCallCapScope? previous)
    {
        AmbientCallCap.Value = previous;
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
}

/// <summary>
///     The handle <see cref="ProviderCallBudget.BeginCallCapScope" /> returns: it tightens the call ceiling for every
///     budget created inside the scope, restores the prior ambient cap when disposed so a nested seed cannot leak into
///     an outer turn, and — the part that makes a step's spend observable — collects the budgets that were created
///     under it so the seeding caller can read back what the run consumed.
///     <para>
///         More than one budget can attach: a run that spawns a sub-agent invocation seeds a second scope, and
///         <see cref="MaxProviderCalls" /> then bounds EACH of them separately rather than their total.
///         <see cref="CaptureConsumption" /> sums, because the honest answer to "what did this step spend" is the
///         total — but it also reports <see cref="ProviderCallConsumption.AttachedBudgets" />, precisely so a reader
///         never divides a summed call count by a per-budget ceiling. Eighteen calls across two budgets is two runs
///         that each stayed under ten, not one run that breached it.
///     </para>
/// </summary>
public sealed class ProviderCallCapScope : IDisposable
{
    private readonly ProviderCallCapScope? _previous;
    private readonly Lock _gate = new();
    private readonly List<ProviderCallBudget> _budgets = [];
    private bool _disposed;

    internal ProviderCallCapScope(int maxProviderCalls, ProviderCallCapScope? previous)
    {
        MaxProviderCalls = maxProviderCalls;
        _previous = previous;
    }

    /// <summary>The per-scope call ceiling this handle seeded. Tighten-only against the configured invocation ceiling.</summary>
    public int MaxProviderCalls { get; }

    /// <summary>
    ///     What the budgets created under this scope have consumed so far, or <see langword="null" /> when none was
    ///     created (nothing ran, or the run never seeded a budget). Counts plus tool IDENTITY — a bounded set of tool
    ///     names — and nothing else: never an argument, never a result, never a prompt or a model's output. Safe to
    ///     persist on that basis, and the reason the names are here at all is that a step's budget is disposed long
    ///     before anything downstream can ask what the step called.
    ///     <para>
    ///         Read it AFTER the run has landed but BEFORE the scope is disposed. Reading it mid-run answers with a
    ///         moving target, and a run stopped through the cancellation registry may still be unwinding, so a
    ///         cancelled step's numbers are a race rather than a measurement. Disposal drops the collected budgets, so
    ///         a read afterwards answers <see langword="null" /> like a scope nothing ever ran under.
    ///     </para>
    /// </summary>
    public ProviderCallConsumption? CaptureConsumption()
    {
        ProviderCallBudget[] budgets;
        lock (_gate)
        {
            if (_budgets.Count == 0)
            {
                return null;
            }

            budgets = [.. _budgets];
        }

        var providerCalls = 0;
        var estimatedInputTokens = 0L;
        var toolCallsCompleted = 0;
        var toolSchemaTokens = 0L;
        var toolNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var budget in budgets)
        {
            var snapshot = budget.CaptureEfficiencySnapshot();
            providerCalls += snapshot.ProviderCalls;
            estimatedInputTokens += snapshot.EstimatedInputTokens;
            toolCallsCompleted += snapshot.ToolCallsCompleted;
            toolSchemaTokens += snapshot.ToolSchemaTokens;

            // Union, then re-cap: each budget is bounded on its own, so a step that spawned three sub-agents could
            // otherwise carry three times the bound out of a scope that is meant to have one.
            toolNames.UnionWith(budget.ToolNames);
        }

        return new ProviderCallConsumption(providerCalls,
            estimatedInputTokens,
            toolCallsCompleted,
            MaxProviderCalls,
            budgets.Length,
            toolSchemaTokens,
            [.. toolNames.Take(ProviderCallBudget.MaxDistinctToolNames)]);
    }

    /// <summary>
    ///     Restores the ambient cap this scope replaced and releases the budgets it collected, so a long-lived caller
    ///     does not pin one run's counters for the life of the process. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _budgets.Clear();
        }

        ProviderCallBudget.RestoreCallCap(_previous);
    }

    /// <summary>
    ///     Registers a budget created under this scope. A late attach — a run still unwinding after the caller stopped
    ///     watching, which is exactly how a cancelled step ends — is IGNORED rather than throwing: this is a telemetry
    ///     sink, and faulting a run that is already on its way out to protect a measurement nobody will read would
    ///     trade a real turn for a number.
    /// </summary>
    internal void Attach(ProviderCallBudget budget)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _budgets.Add(budget);
        }
    }
}

/// <summary>
///     What one caller-capped run consumed: counts, plus the bounded set of tool NAMES it called — never prompts,
///     model output, tool arguments, results, paths or schemas. Small enough to persist on an event row, which is what
///     the work-session supervisor does with it so a per-step cap can be sized from recorded data rather than guessed,
///     and so a cost rollup can answer "which tools" as well as "how many calls" once the run's budget is long gone.
/// </summary>
/// <param name="ProviderCalls">Raw provider rounds that were admitted (the rejected one that tripped a ceiling is not counted), summed over every attached budget.</param>
/// <param name="EstimatedInputTokens">Estimated input tokens across those rounds — an estimate from the character profile, not the provider's count.</param>
/// <param name="ToolCallsCompleted">Tool invocations that returned, successfully or not.</param>
/// <param name="ProviderCallCap">
///     The ceiling the caller seeded. It bounds EACH attached budget, not their sum, so it is only a denominator for
///     <paramref name="ProviderCalls" /> while <paramref name="AttachedBudgets" /> is 1.
/// </param>
/// <param name="AttachedBudgets">
///     How many invocations ran under the scope — 1 for an ordinary run, more when it spawned sub-agent invocations,
///     each of which got its own budget and its own ceiling. Reported so nobody reads a summed call count as a
///     breached cap.
/// </param>
/// <param name="ToolSchemaTokens">
///     Tool-schema tokens SHIPPED ACROSS ROUNDS — every round re-sends the whole offer, so this grows with the number
///     of rounds and is not the size of the offer. The largest single round is a different number.
/// </param>
/// <param name="ToolNames">
///     The distinct tool names the run called, ordinal-sorted and capped at
///     <c>ProviderCallBudget.MaxDistinctToolNames</c> across every attached budget. Names only.
/// </param>
public sealed record ProviderCallConsumption(
    int ProviderCalls,
    long EstimatedInputTokens,
    int ToolCallsCompleted,
    int ProviderCallCap,
    int AttachedBudgets,
    long ToolSchemaTokens = 0,
    IReadOnlyList<string>? ToolNames = null);

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
