namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Raised at the raw provider boundary when a SINGLE round's message set still exceeds the effective context window
///     after the per-round budgeter has reduced everything it can.
/// </summary>
/// <remarks>
///     The pinned set alone — system messages, recent-keep window, pending tool result — is then larger than the
///     window (<see cref="Chat.ProviderBudgetResult.ExceedsWindow" /> stayed true). Thrown BEFORE the provider call
///     rather than shipping a round that would overrun the launched window (llama-server's <c>-c</c>) or be rejected
///     deep inside with an opaque error. It bounds one irreducible round, where
///     <see cref="ProviderCallBudgetExceededException" /> bounds a runaway loop.
/// </remarks>
public sealed class ProviderContextWindowExceededException : InvalidOperationException
{
    /// <summary>
    ///     Fixed, path-free terminal message surfaced when a single round cannot be reduced under the window. Carries no
    ///     token counts, model names, or content — safe to forward to the caller verbatim.
    /// </summary>
    public const string RoundExceedsWindowMessage =
        "This request is too large for the model's context window even after trimming the conversation — start a new chat or switch to a larger-context model.";

    public ProviderContextWindowExceededException(int estimatedTokens, int windowTokens)
        : base(RoundExceedsWindowMessage)
    {
        EstimatedTokens = estimatedTokens;
        WindowTokens = windowTokens;
    }

    /// <summary>Estimated input tokens of the irreducible round (a bounded number for diagnostics; not in the surfaced message).</summary>
    public int EstimatedTokens { get; }

    /// <summary>Effective context window the round was budgeted against (a bounded number for diagnostics; not in the surfaced message).</summary>
    public int WindowTokens { get; }
}
