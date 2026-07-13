namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Bounds applied at the RAW provider boundary — the innermost pipeline hop, below automatic function invocation —
///     so that EVERY inner tool-loop round (and every MAF participant turn) is re-budgeted before it reaches the model,
///     not just the two outer history-growth points the invocation runner already budgets. Bound from the
///     <c>Agent:ProviderCallBudget</c> section. Two independent classes of guard live here:
///     <list type="bullet">
///         <item>
///             <description>
///                 Per-round input budgeting (<see cref="DefaultContextTokens" />, <see cref="ReservedOutputTokenFloor" />,
///                 <see cref="RecentMessagesToKeep" />, <see cref="OversizedToolResultExcerptChars" />): bounds the
///                 message set of a SINGLE provider round to the launched context window, excerpting oversized tool
///                 results (never dropping the pending one) and dropping the oldest non-protected history.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Cumulative per-invocation ceilings (<see cref="MaxProviderCallsPerInvocation" />,
///                 <see cref="MaxCumulativeInputTokens" />): a runaway-loop backstop that terminates an invocation whose
///                 autonomous rounds (across the single-agent loop, approval resumes, and orchestration participants)
///                 exceed a total call count or total estimated input-token spend, with a clean typed failure.
///             </description>
///         </item>
///     </list>
/// </summary>
public sealed class ProviderCallBudgetOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string Section = "Agent:ProviderCallBudget";

    /// <summary>
    ///     Hard ceiling on the total number of raw provider rounds one invocation may make, counted cumulatively across
    ///     the tool-calling loop, approval resumes, AND every orchestration participant turn. Deliberately well above the
    ///     per-request tool-iteration cap (<see cref="AgentToolPipelineOptions.MaximumToolIterationsPerRequest" />, 40) so
    ///     a normal multi-participant turn is never affected; it only fires on a genuine runaway. Default 200.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxProviderCallsPerInvocation { get; set; } = 200;

    /// <summary>
    ///     Hard ceiling on the total estimated INPUT tokens summed across every provider round of one invocation. A
    ///     second runaway backstop independent of the call count: a loop that keeps each round under the window but
    ///     accumulates unbounded total spend is still terminated. Default 4,000,000.
    /// </summary>
    [Range(1024, int.MaxValue)]
    public int MaxCumulativeInputTokens { get; set; } = 4_000_000;

    /// <summary>
    ///     Fallback context-window size (tokens) used for the per-round input budget when the round's
    ///     <c>ChatOptions</c> carries no explicit <c>num_ctx</c> override. Kept equal to the outer budgeter's default so
    ///     the two boundaries agree. Must be at least 1.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int DefaultContextTokens { get; set; } = 8192;

    /// <summary>Tokens reserved from the window for the model's response before the input is measured. Widened by any explicit per-round max-output-tokens.</summary>
    [Range(0, int.MaxValue)]
    public int ReservedOutputTokenFloor { get; set; } = 1024;

    /// <summary>
    ///     How many of the most recent non-system messages are always kept and never dropped (they carry the in-flight
    ///     call/result round). The very last message is always kept regardless (the pending tool result), so this never
    ///     drops what the model must see next. Must be at least 2 so a call and its immediately following result survive.
    /// </summary>
    [Range(2, int.MaxValue)]
    public int RecentMessagesToKeep { get; set; } = 6;

    /// <summary>
    ///     Character budget an oversized tool result (anywhere in the round, including a recent one) is excerpted down to,
    ///     with a structured omitted-count marker appended, before whole history messages are dropped. Zero collapses an
    ///     oversized result to just the marker. This is the primary size backstop that keeps the pending tool result
    ///     bounded rather than dropped.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int OversizedToolResultExcerptChars { get; set; } = 2000;
}
