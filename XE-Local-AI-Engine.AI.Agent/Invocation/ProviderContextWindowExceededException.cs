namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Raised at the raw provider boundary when a SINGLE round's message set still exceeds the effective context window
///     after the deterministic per-round budgeter has reduced everything it can — i.e. the pinned set alone (system
///     messages, the recent-keep window, and the pending tool result) is larger than the window
///     (<see cref="Chat.ProviderBudgetResult.ExceedsWindow" /> stayed true). Thrown BEFORE the offending provider call so
///     the round is failed cleanly with a classified, sanitized message instead of being shipped to the provider, where it
///     would overrun the launched context window (llama-server's <c>-c</c>) or be rejected deep inside with an opaque
///     error. This bounds a single irreducible round, complementing <see cref="ProviderCallBudgetExceededException" />,
///     which bounds the cumulative call/token spend of a runaway loop.
///     <para>
///         The <see cref="Exception.Message" /> is a fixed, path-free constant carrying no token counts, model names, or
///         content, so it is safe to surface verbatim. The bounded <see cref="EstimatedTokens" /> and
///         <see cref="WindowTokens" /> numbers are exposed as properties for server-side diagnostics only and are never
///         folded into the surfaced message.
///     </para>
/// </summary>
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
