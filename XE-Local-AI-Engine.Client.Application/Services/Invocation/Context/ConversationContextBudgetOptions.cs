namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

/// <summary>
///     Operator-tunable knobs for deterministic input-context budgeting applied to the conversation history sent to the
///     provider on each invocation turn. These are node-level operational settings (NOT part of a runtime package's
///     cross-repo config hash), bound from the <c>Agent:ConversationContextBudget</c> configuration section. Defaults
///     are on and conservative so a fresh install bounds long conversations without an operator having to opt in.
/// </summary>
public sealed class ConversationContextBudgetOptions
{
    public const string SectionName = "Agent:ConversationContextBudget";

    /// <summary>
    ///     The default of <see cref="HistoricalToolResultExcerptChars" />, exposed as a constant so the callers that
    ///     apply the same cap before the budgeter ever sees the round — <c>ConversationContextBuilder.Build</c>'s tool
    ///     history projection and the step bound's estimate of it — can carry it as a parameter default instead of
    ///     re-typing the number and drifting from it.
    /// </summary>
    public const int DefaultHistoricalToolResultExcerptChars = 2000;

    /// <summary>
    ///     Minimum number of output tokens reserved from the context window before history is measured, so the model
    ///     always has room to answer. The runner takes the larger of this floor and any explicit per-send
    ///     max-output-tokens override.
    /// </summary>
    public int ReservedOutputTokenFloor { get; set; } = 1024;

    /// <summary>
    ///     How many of the most recent turns (a user message plus every assistant/tool message that follows it up to the
    ///     next user message) are always kept and never trimmed. Guarantees the latest user message and the in-flight
    ///     tool-calling round survive budgeting. Must be at least 2: the approval-replay path spans two turns — the
    ///     assistant tool-call and its approval request land in one turn, and the replayed User approval-decision lands
    ///     in the next — so protecting a single turn could drop the tool-call turn and orphan the approval response. The
    ///     budgeter clamps to this floor as well, so even a mis-set config cannot orphan an approval round.
    /// </summary>
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
    ///     Enables the budgeter's Pass 4: when the ordinary passes (excerpt historical tool results, drop whole historical
    ///     turns, evict whole historical approval groups) still leave the round over budget, strip
    ///     <see cref="Microsoft.Extensions.AI.TextReasoningContent" /> from surviving messages OLDEST FIRST, and only for
    ///     as long as the round is still over budget. The last surviving message is never touched. This is the first pass
    ///     allowed to reach into the protected recent window, and the only content it takes there is the model's own
    ///     superseded scratch-pad thinking — never a tool call, a tool result, or an approval record — so it cannot orphan
    ///     a correlation. It fires ONLY in rounds that would otherwise raise
    ///     <see cref="ContextBudgetExceededException" /> and fail the turn outright.
    ///     <para>
    ///         Default ON. It shipped off and was flipped only once the combined replay gate
    ///         (<c>BudgetedApprovalReplayTests</c>) proved a history it rewrote still survives the approval validator,
    ///         function invocation, the inner provider-call budgeter and the real OpenAI/llama-server wire adapter. What
    ///         it discards is informationally inert; the alternative in exactly these rounds is a failed turn.
    ///     </para>
    /// </summary>
    public bool StripProtectedReasoning { get; set; } = true;

    /// <summary>
    ///     Enables the budgeter's Pass 5: when Pass 4 still leaves the round over budget, excerpt oversized tool results
    ///     inside the PROTECTED recent window (the same excerpt + omitted-count marker Pass 1 applies to historical ones),
    ///     oldest first, only while still over budget, and never on the last surviving message. Default OFF: unlike Pass 4
    ///     this shortens content the model is actively working with, so it is an explicit operator opt-in rather than a
    ///     silent behaviour change to the "protected recent turns are never modified" invariant. The replay gate covers it
    ///     too — the default is a deliberate policy choice, not an unproven pass.
    /// </summary>
    public bool ExcerptProtectedToolResults { get; set; }
}
