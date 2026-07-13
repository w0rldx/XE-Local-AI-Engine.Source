namespace XE_Local_AI_Engine.AI.Agent.Invocation;

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

    // The single ambient slot. AsyncLocal flows the value into every continuation the run awaits, including the
    // function-invocation pipeline's inner provider rounds and the MAF workflow's participant turns, so the middleware
    // reads the invocation's shared counters without threading a parameter through the MAF/IChatClient surface.
    private static readonly AsyncLocal<ProviderCallBudget?> AmbientBudget = new();

    private readonly int _maxProviderCalls;
    private readonly int _maxCumulativeInputTokens;
    private int _providerCalls;
    private long _cumulativeInputTokens;

    private ProviderCallBudget(ProviderCallBudgetOptions options)
    {
        Options = options;
        _maxProviderCalls = options.MaxProviderCallsPerInvocation;
        _maxCumulativeInputTokens = options.MaxCumulativeInputTokens;
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
        ArgumentNullException.ThrowIfNull(options);

        var previous = AmbientBudget.Value;
        AmbientBudget.Value = new ProviderCallBudget(options);
        return new Scope(previous);
    }

    /// <summary>
    ///     Registers one raw provider round of <paramref name="estimatedInputTokens" /> against the cumulative ceilings.
    ///     Throws <see cref="ProviderCallBudgetExceededException" /> when either ceiling is exceeded, so the middleware can
    ///     fail the round BEFORE calling the provider rather than after. Atomic increments make it safe across the
    ///     concurrent participant runs an orchestration may drive.
    /// </summary>
    public void RegisterProviderRound(int estimatedInputTokens)
    {
        var calls = Interlocked.Increment(ref _providerCalls);
        var tokens = Interlocked.Add(ref _cumulativeInputTokens, Math.Max(0, estimatedInputTokens));

        if (calls > _maxProviderCalls || tokens > _maxCumulativeInputTokens)
        {
            throw new ProviderCallBudgetExceededException(CeilingExceededMessage);
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
