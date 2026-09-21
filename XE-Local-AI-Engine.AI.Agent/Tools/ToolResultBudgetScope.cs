namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     Per-invocation tightening of the shared tool-result character budget
///     (<c>Agent:ToolPipeline:MaxToolResultCharacters</c>), flowed as an <see cref="AsyncLocal{T}" /> in the same shape
///     as <c>AgentRunConversationContext</c> and <c>SpawnContext</c>.
/// </summary>
/// <remarks>
///     The shared budget is read once when the tool registries are constructed, so it is a node-wide constant a caller
///     needing a smaller ceiling for ONE run cannot get by re-reading options. A work-session step is that caller: its
///     knowledge-base reads return up to 50,000 characters each, and several in one step overrun a 65,536-token window.
///     <b>Tighten-only</b>, like the tool-approval policy — the ambient value applies only when it is SMALLER than the
///     configured budget, and a missing scope means "no override", so every other path stays byte-identical.
/// </remarks>
public static class ToolResultBudgetScope
{
    // The single ambient slot. AsyncLocal flows the value into every continuation the run awaits, including the
    // function-invocation pipeline's tool calls, so the wrapper reads it without a parameter on the MAF tool surface.
    private static readonly AsyncLocal<int?> AmbientMaxResultCharacters = new();

    /// <summary>The tightened budget for the current async flow, or <see langword="null" /> when none was seeded.</summary>
    public static int? Current => AmbientMaxResultCharacters.Value;

    /// <summary>
    ///     Seeds a tightened tool-result budget for the current async flow and returns a scope whose disposal restores
    ///     the prior ambient value.
    /// </summary>
    /// <remarks>
    ///     The prior value is restored rather than cleared, so a nested seed cannot leak into an outer turn.
    /// </remarks>
    public static IDisposable BeginScope(int maxResultCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultCharacters);

        var previous = AmbientMaxResultCharacters.Value;
        AmbientMaxResultCharacters.Value = maxResultCharacters;
        return new Scope(previous);
    }

    /// <summary>
    ///     The budget to actually apply: the ambient value when one is seeded AND it is tighter than
    ///     <paramref name="configuredMaxResultCharacters" />, otherwise the configured value unchanged.
    /// </summary>
    public static int Resolve(int configuredMaxResultCharacters)
    {
        return AmbientMaxResultCharacters.Value is { } ambient && ambient < configuredMaxResultCharacters
            ? ambient
            : configuredMaxResultCharacters;
    }

    // Restores the prior ambient budget when disposed. Idempotent: a double-dispose re-restores the same value.
    private sealed class Scope : IDisposable
    {
        private readonly int? _previous;

        public Scope(int? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            AmbientMaxResultCharacters.Value = _previous;
        }
    }
}
