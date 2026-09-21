namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using System.Diagnostics;
using XE_Local_AI_Engine.AI.Agent.Configuration;

/// <summary>
///     Per-invocation provider-budget state, flowed through the whole agent run as an <see cref="AsyncLocal{T}" />,
///     mirroring how <c>SpawnContext</c> flows the spawn caps.
/// </summary>
/// <remarks>
///     The invocation runner seeds one scope per turn, and the provider-boundary budget middleware reads it on EVERY
///     raw provider round — the inner tool-calling rounds that never surface to the runner included, and every MAF
///     participant turn — to enforce the cumulative call-count and input-token ceilings. A missing ambient scope means
///     "no budget" (the eval and preview-workflow runners drive the shared client without one), so the middleware
///     degrades to a pass-through.
/// </remarks>
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
    ///     (<see cref="BeginCallCapScope" />) tighter than the configured invocation ceiling.
    /// </summary>
    /// <remarks>
    ///     That is a bound being spent, not a runaway loop — the work-session supervisor ends the step and the next one
    ///     resumes from the saved state — so the copy must not read as a fault. Pinned by
    ///     <c>ProviderCallBudgetTests.RegisterProviderRound_WhenTheStepCapTrips_ReportsTheStepMessage</c> and matched
    ///     verbatim by the supervisor and by the chat pane (<c>ChatMessage.tsx</c>).
    /// </remarks>
    public const string StepCallCapReachedMessage =
        "This step reached its provider-call cap; the work session continues from its saved state on the next step.";

    // The single ambient slot: AsyncLocal flows the value into every continuation the run awaits, so the middleware
    // reads the invocation's shared counters without threading a parameter through the MAF/IChatClient surface.
    private static readonly AsyncLocal<ProviderCallBudget?> AmbientBudget = new();

    // A caller-seeded TIGHTENING of MaxProviderCallsPerInvocation, for a work-session step whose re-sent tool results
    // overrun the window inside its own loop. A scope OBJECT: an AsyncLocal write inside a run never propagates OUT.
    private static readonly AsyncLocal<ProviderCallCapScope?> AmbientCallCap = new();

    /// <summary>
    ///     How many DISTINCT tool names one budget keeps — and, deliberately, the ONE number anything downstream caps
    ///     to as well.
    /// </summary>
    /// <remarks>
    ///     Bounded here rather than at a reader, so a runaway tool loop cannot grow the set: the seventeenth distinct
    ///     name is dropped at the source, not on the way out.
    /// </remarks>
    internal const int MaxDistinctToolNames = 16;

    /// <summary>How long ONE recorded tool name may be, marker included.</summary>
    /// <remarks>
    ///     Bounded at the source for the same reason the distinct-name count is: that caps how MANY names a carrier
    ///     holds, and only this caps how BIG each is. Without it a single oversized identifier reaches the persisted
    ///     step detail, and from there the work-session event detail, unbounded — the node-run column's own
    ///     1024-character clamp is applied later and only on that one carrier.
    /// </remarks>
    internal const int MaxToolNameLength = 128;

    /// <summary>The last characters of a clamped name. Unmistakably not part of any real tool identifier.</summary>
    internal const string TruncatedToolNameMarker = "…";

    private readonly int _maxProviderCalls;

    // Names only, ordinal-sorted, bounded: the counters beside it say how MUCH a run spent, this says WHICH tools it
    // reached for — a fixed built-in id or an operator-authored MCP/custom identifier, never an argument or a result.
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

    /// <summary>Seeds a scope whose latency baselines start at a caller-owned timestamp.</summary>
    /// <remarks>
    ///     The production invocation runner passes its turn-start timestamp, so time-to-first-tool includes the
    ///     admission and context work done before the budget scope itself is created; direct and test callers use
    ///     <see cref="BeginScope(ProviderCallBudgetOptions)" />.
    /// </remarks>
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
    ///     Tightens <see cref="ProviderCallBudgetOptions.MaxProviderCallsPerInvocation" /> for every scope created in
    ///     the current async flow, and returns a disposable restoring the prior value.
    /// </summary>
    /// <remarks>
    ///     Seed it BEFORE the run begins: the runner creates its own scope from the node options, so this is the only
    ///     way an outer caller can cap one run. <b>Tighten-only</b> — a value at or above the configured ceiling is
    ///     ignored. The handle is also the only readable seam for what the run spent, because an
    ///     <see cref="AsyncLocal{T}" /> written inside the run does not flow back: every budget created inside registers
    ///     itself, and <see cref="ProviderCallCapScope.CaptureConsumption" /> answers once the run has landed.
    /// </remarks>
    public static ProviderCallCapScope BeginCallCapScope(int maxProviderCalls)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxProviderCalls);

        var scope = new ProviderCallCapScope(maxProviderCalls, AmbientCallCap.Value);
        AmbientCallCap.Value = scope;
        return scope;
    }

    /// <summary>
    ///     Registers one raw provider round of <paramref name="estimatedInputTokens" /> against the cumulative ceilings,
    ///     throwing <see cref="ProviderCallBudgetExceededException" /> when either is exceeded.
    /// </summary>
    /// <remarks>
    ///     Throwing lets the middleware fail the round BEFORE calling the provider rather than after. Atomic increments
    ///     make it safe across the concurrent participant runs an orchestration may drive.
    /// </remarks>
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

    /// <summary>Counts one requested tool call, and records its name into the budget's bounded distinct-name set.</summary>
    /// <remarks>
    ///     The names turn "this step made nine tool calls" into "and they were these four tools", which is what a cost
    ///     rollup gets asked. A resolved name is clamped to <see cref="MaxToolNameLength" /> HERE rather than at a
    ///     reader, so every downstream carrier — persisted step detail, work-session event detail, node-run column — is
    ///     bounded by construction.
    /// </remarks>
    /// <param name="toolName">
    ///     The tool the model asked for; null or blank records the count alone. That is what the caller passes for a
    ///     name that did NOT resolve against the offered tools.
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

        return new ProviderCallEfficiencySnapshot
        {
            ProviderCalls = Math.Max(0, attempts - rejected),
            ProviderRoundsRejected = rejected,
            EstimatedInputTokens = Math.Max(0, Interlocked.Read(ref _cumulativeInputTokens) - Interlocked.Read(ref _rejectedInputTokens)),
            MaximumEstimatedInputTokens = Volatile.Read(ref _maximumEstimatedInputTokens),
            ToolSchemaTokens = Interlocked.Read(ref _toolSchemaTokens),
            MaximumToolSchemaTokens = Volatile.Read(ref _maximumToolSchemaTokens),
            ProviderRoundElapsedMs = FromMicroseconds(Interlocked.Read(ref _providerRoundElapsedMicroseconds)),
            MessagesDropped = Interlocked.Read(ref _messagesDropped),
            ToolResultsTruncated = Interlocked.Read(ref _toolResultsTruncated),
            CharsTruncated = Interlocked.Read(ref _charsTruncated),
            ToolCallsRequested = Volatile.Read(ref _toolCallsRequested),
            ToolCallsCompleted = Volatile.Read(ref _toolCallsCompleted),
            ToolCallsFailed = Volatile.Read(ref _toolCallsFailed),
            ToolRequestToResultMs = FromMicroseconds(Interlocked.Read(ref _toolRequestToResultMicroseconds)),
            ToolResultBytes = Interlocked.Read(ref _toolResultBytes),
            TimeToFirstToolRequestMs = firstToolRequestMicroseconds < 0 ? null : FromMicroseconds(firstToolRequestMicroseconds),
            ProviderRetries = Volatile.Read(ref _providerRetries),
            ToolArgumentRepairs = Volatile.Read(ref _toolArgumentRepairs),
            AgentHandoffs = Volatile.Read(ref _agentHandoffs)
        };
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
///     budget created inside the scope, restores the prior ambient cap on dispose, and collects those budgets so the
///     seeding caller can read back what the run consumed.
/// </summary>
/// <remarks>
///     More than one budget can attach — a run that spawns a sub-agent invocation seeds a second scope — and
///     <see cref="MaxProviderCalls" /> then bounds EACH of them separately rather than their total.
///     <see cref="CaptureConsumption" /> sums, but also reports
///     <see cref="ProviderCallConsumption.AttachedBudgets" />, precisely so a reader never divides a summed call count
///     by a per-budget ceiling. See docs/wiki/04-agent-mode.md ("The per-step consumption record").
/// </remarks>
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
    ///     created — nothing ran, or the run never seeded a budget.
    /// </summary>
    /// <remarks>
    ///     Counts plus tool IDENTITY (a bounded set of names) and nothing else: never an argument, result, prompt or
    ///     model output, so it is safe to persist — which is why the names are here at all, a step's budget being
    ///     disposed long before anything downstream can ask what it called. Read it AFTER the run has landed but BEFORE
    ///     the scope is disposed: mid-run is a moving target, a cancelled run may still be unwinding, and disposal
    ///     drops the collected budgets so a later read answers <see langword="null" />.
    /// </remarks>
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

        return new ProviderCallConsumption
        {
            ProviderCalls = providerCalls,
            EstimatedInputTokens = estimatedInputTokens,
            ToolCallsCompleted = toolCallsCompleted,
            ProviderCallCap = MaxProviderCalls,
            AttachedBudgets = budgets.Length,
            ToolSchemaTokens = toolSchemaTokens,
            ToolNames = [.. toolNames.Take(ProviderCallBudget.MaxDistinctToolNames)]
        };
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

    /// <summary>Registers a budget created under this scope.</summary>
    /// <remarks>
    ///     A late attach — a run still unwinding after the caller stopped watching, which is exactly how a cancelled
    ///     step ends — is IGNORED rather than throwing: this is a telemetry sink, and faulting a run already on its way
    ///     out to protect a measurement nobody will read would trade a real turn for a number.
    /// </remarks>
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
///     model output, tool arguments, results, paths or schemas.
/// </summary>
/// <remarks>
///     Small enough to persist on an event row, which is what the work-session supervisor does with it, so a per-step
///     cap can be sized from recorded data rather than guessed and a cost rollup can answer "which tools" as well as
///     "how many calls" once the run's budget is long gone.
/// </remarks>
public sealed class ProviderCallConsumption
{
    /// <summary>Raw provider rounds that were admitted (the rejected one that tripped a ceiling is not counted), summed over every attached budget.</summary>
    public required int ProviderCalls { get; init; }

    /// <summary>Estimated input tokens across those rounds — an estimate from the character profile, not the provider's count.</summary>
    public required long EstimatedInputTokens { get; init; }

    /// <summary>Tool invocations that returned, successfully or not.</summary>
    public required int ToolCallsCompleted { get; init; }

    /// <summary>
    ///     The ceiling the caller seeded. It bounds EACH attached budget, not their sum, so it is only a denominator for
    ///     <see cref="ProviderCalls" /> while <see cref="AttachedBudgets" /> is 1.
    /// </summary>
    public required int ProviderCallCap { get; init; }

    /// <summary>
    ///     How many invocations ran under the scope — 1 for an ordinary run, more when it spawned sub-agent
    ///     invocations, each with its own budget and its own ceiling.
    /// </summary>
    /// <remarks>Reported so nobody reads a summed call count as a breached cap.</remarks>
    public required int AttachedBudgets { get; init; }

    /// <summary>Tool-schema tokens SHIPPED ACROSS ROUNDS.</summary>
    /// <remarks>
    ///     Every round re-sends the whole offer, so this grows with the number of rounds and is not the size of the
    ///     offer. The largest single round is a different number.
    /// </remarks>
    public long ToolSchemaTokens { get; init; }

    /// <summary>
    ///     The distinct tool names the run called, ordinal-sorted and capped at
    ///     <c>ProviderCallBudget.MaxDistinctToolNames</c> across every attached budget. Names only.
    /// </summary>
    public IReadOnlyList<string>? ToolNames { get; init; }
}

/// <summary>
///     Immutable, content-free aggregate of the expensive work performed during one root agent invocation: counts,
///     durations and estimated sizes only.
/// </summary>
/// <remarks>
///     Never prompts, model output, tool identities, arguments, results, paths or schemas, so the invocation runner can
///     export it safely through bounded telemetry.
/// </remarks>
internal sealed class ProviderCallEfficiencySnapshot
{
    public required int ProviderCalls { get; init; }

    public required int ProviderRoundsRejected { get; init; }

    public required long EstimatedInputTokens { get; init; }

    public required int MaximumEstimatedInputTokens { get; init; }

    public required long ToolSchemaTokens { get; init; }

    public required int MaximumToolSchemaTokens { get; init; }

    public required double ProviderRoundElapsedMs { get; init; }

    public required long MessagesDropped { get; init; }

    public required long ToolResultsTruncated { get; init; }

    public required long CharsTruncated { get; init; }

    public required int ToolCallsRequested { get; init; }

    public required int ToolCallsCompleted { get; init; }

    public required int ToolCallsFailed { get; init; }

    public required double ToolRequestToResultMs { get; init; }

    public required long ToolResultBytes { get; init; }

    public required double? TimeToFirstToolRequestMs { get; init; }

    public required int ProviderRetries { get; init; }

    public required int ToolArgumentRepairs { get; init; }

    public required int AgentHandoffs { get; init; }
}
