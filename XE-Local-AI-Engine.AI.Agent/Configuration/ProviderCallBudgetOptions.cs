namespace XE_Local_AI_Engine.AI.Agent.Configuration;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Bounds applied at the RAW provider boundary, so EVERY inner tool-loop round and MAF participant turn is
///     re-budgeted, not just the two outer history-growth points the invocation runner already budgets.
/// </summary>
/// <remarks>
///     Bound from the <c>Agent:ProviderCallBudget</c> section, and two independent classes of guard. Per-round input
///     budgeting (<see cref="DefaultContextTokens" />, <see cref="ReservedOutputTokenFloor" />,
///     <see cref="RecentMessagesToKeep" />, <see cref="OversizedToolResultExcerptChars" />) fits one round to the
///     launched window; cumulative ceilings (<see cref="MaxProviderCallsPerInvocation" />,
///     <see cref="MaxCumulativeInputTokens" />) terminate a runaway invocation with a clean typed failure.
/// </remarks>
public sealed class ProviderCallBudgetOptions
{
    public const string Section = "Agent:ProviderCallBudget";

    /// <summary>
    ///     Hard ceiling on the raw provider rounds one invocation may make, counted across the tool-calling loop,
    ///     approval resumes AND every orchestration participant turn. Default 200.
    /// </summary>
    /// <remarks>
    ///     Deliberately well above the per-request tool-iteration cap
    ///     (<see cref="AgentToolPipelineOptions.MaximumToolIterationsPerRequest" />, 40), so a normal multi-participant
    ///     turn is never affected and it only fires on a genuine runaway.
    /// </remarks>
    [Range(1, 100_000)]
    public int MaxProviderCallsPerInvocation { get; set; } = 200;

    /// <summary>
    ///     Hard ceiling on the total estimated INPUT tokens summed across every provider round of one invocation.
    ///     Default 4,000,000.
    /// </summary>
    /// <remarks>
    ///     A second runaway backstop, independent of the call count: a loop that keeps each round under the window but
    ///     accumulates unbounded total spend is still terminated.
    /// </remarks>
    [Range(1024, int.MaxValue)]
    public int MaxCumulativeInputTokens { get; set; } = 4_000_000;

    /// <summary>
    ///     Fallback context-window size in tokens for the per-round input budget when the round's <c>ChatOptions</c>
    ///     carries no explicit <c>num_ctx</c> override. Must be at least 1.
    /// </summary>
    /// <remarks>Kept equal to the outer budgeter's default so the two boundaries agree.</remarks>
    [Range(1, int.MaxValue)]
    public int DefaultContextTokens { get; set; } = 8192;

    /// <summary>Tokens reserved from the window for the model's response before the input is measured. Widened by any explicit per-round max-output-tokens.</summary>
    [Range(0, int.MaxValue)]
    public int ReservedOutputTokenFloor { get; set; } = 1024;

    /// <summary>
    ///     How many of the most recent non-system messages are always kept and never dropped, because they carry the
    ///     in-flight call/result round. Must be at least 2, so a call and its following result survive.
    /// </summary>
    /// <remarks>
    ///     The very last message — the pending tool result — is kept regardless, so this never drops what the model
    ///     must see next.
    /// </remarks>
    [Range(2, int.MaxValue)]
    public int RecentMessagesToKeep { get; set; } = 6;

    /// <summary>
    ///     Character budget an oversized tool result — anywhere in the round, including a recent one — is excerpted
    ///     down to, with an omitted-count marker appended, before whole history messages are dropped.
    /// </summary>
    /// <remarks>
    ///     Zero collapses an oversized result to just the marker. This is the primary size backstop, and it is what
    ///     keeps the pending tool result bounded rather than dropped.
    /// </remarks>
    [Range(0, int.MaxValue)]
    public int OversizedToolResultExcerptChars { get; set; } = 2000;
}
