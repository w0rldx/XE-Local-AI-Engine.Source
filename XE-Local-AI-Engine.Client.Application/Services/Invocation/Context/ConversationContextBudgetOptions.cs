namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

/// <summary>
///     Operator-tunable knobs for the deterministic input-context budgeting applied to the conversation history sent
///     to the provider on each invocation turn.
/// </summary>
/// <remarks>
///     Node-level operational settings, NOT part of a runtime package's cross-repo config hash, bound from the
///     <c>Agent:ConversationContextBudget</c> section. Defaults are on and conservative, so a fresh install bounds
///     long conversations without an operator having to opt in.
/// </remarks>
public sealed class ConversationContextBudgetOptions
{
    public const string SectionName = "Agent:ConversationContextBudget";

    /// <summary>The default of <see cref="HistoricalToolResultExcerptChars" />, exposed as a constant.</summary>
    /// <remarks>
    ///     The callers that apply the same cap before the budgeter sees the round — <c>ConversationContextBuilder.Build</c>'s
    ///     tool-history projection and the step bound's estimate of it — carry it as a parameter default instead of
    ///     re-typing the number and drifting from it.
    /// </remarks>
    public const int DefaultHistoricalToolResultExcerptChars = 2000;

    /// <summary>
    ///     Minimum number of output tokens reserved from the context window before history is measured, so the model
    ///     always has room to answer. The runner takes the larger of this floor and any explicit per-send
    ///     max-output-tokens override.
    /// </summary>
    public int ReservedOutputTokenFloor { get; set; } = 1024;

    /// <summary>
    ///     How many of the most recent turns are always kept and never trimmed, which is what guarantees the latest
    ///     user message and the in-flight tool-calling round survive budgeting.
    /// </summary>
    /// <remarks>
    ///     A turn is a user message plus every assistant/tool message up to the next user message. Must be at least 2,
    ///     because the approval-replay path spans two turns — the assistant tool-call and its approval request in one,
    ///     the replayed User decision in the next — so protecting a single turn could drop the tool-call turn and
    ///     orphan the response. The budgeter clamps to that floor too, so a mis-set config cannot orphan a round.
    /// </remarks>
    public int RecentTurnKeepCount { get; set; } = 4;

    /// <summary>
    ///     Character budget an oversized historical tool result is truncated down to (an explicit omitted-count marker is
    ///     appended) before whole turns are dropped. Zero collapses an oversized historical tool result to just the
    ///     marker.
    /// </summary>
    public int HistoricalToolResultExcerptChars { get; set; } = DefaultHistoricalToolResultExcerptChars;

    /// <summary>
    ///     Fallback context-window size (in tokens) used when the package carries no explicit <c>num_ctx</c> override, so
    ///     capacity can still be derived without probing the model. Must be at least 1.
    /// </summary>
    public int DefaultContextTokens { get; set; } = 8192;

    /// <summary>
    ///     Enables the budgeter's Pass 4: stripping <see cref="Microsoft.Extensions.AI.TextReasoningContent" /> from
    ///     surviving messages, oldest first, while the round is still over budget.
    /// </summary>
    /// <remarks>
    ///     The first pass allowed to reach into the protected recent window, and the only content it takes there is the
    ///     model's own superseded scratch-pad thinking — never a tool call, result or approval record — so it cannot
    ///     orphan a correlation; the last surviving message is never touched. It fires ONLY in rounds that would
    ///     otherwise raise <see cref="ContextBudgetExceededException" /> and fail the turn. Default ON: what it
    ///     discards is informationally inert, and <c>BudgetedApprovalReplayTests</c> covers the rewritten history.
    /// </remarks>
    public bool StripProtectedReasoning { get; set; } = true;

    /// <summary>
    ///     Enables the budgeter's Pass 5: excerpting oversized tool results inside the PROTECTED recent window when
    ///     Pass 4 still leaves the round over budget.
    /// </summary>
    /// <remarks>
    ///     It applies the excerpt and omitted-count marker Pass 1 uses on historical results, oldest first, only while
    ///     still over budget, and never on the last surviving message. Default OFF: unlike Pass 4 it shortens content
    ///     the model is actively working with, so relaxing the "protected recent turns are never modified" invariant is
    ///     an explicit operator opt-in. The replay gate covers it too — the default is policy, not an unproven pass.
    /// </remarks>
    public bool ExcerptProtectedToolResults { get; set; }
}
