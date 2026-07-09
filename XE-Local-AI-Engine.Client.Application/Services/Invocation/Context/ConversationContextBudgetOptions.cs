namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

/// <summary>
///     Operator-tunable knobs for deterministic input-context budgeting applied to the conversation history sent to the
///     provider on each invocation turn. These are node-level operational settings (NOT part of a runtime package's
///     cross-repo config hash), bound from the <c>Agent:ConversationContextBudget</c> configuration section. Defaults
///     are on and conservative so a fresh install bounds long conversations without an operator having to opt in.
/// </summary>
public sealed class ConversationContextBudgetOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Agent:ConversationContextBudget";

    /// <summary>
    ///     Minimum number of output tokens reserved from the context window before history is measured, so the model
    ///     always has room to answer. The runner takes the larger of this floor and any explicit per-send
    ///     max-output-tokens override.
    /// </summary>
    public int ReservedOutputTokenFloor { get; set; } = 1024;

    /// <summary>
    ///     How many of the most recent turns (a user message plus every assistant/tool message that follows it up to the
    ///     next user message) are always kept and never trimmed. Guarantees the latest user message and the in-flight
    ///     tool-calling round survive budgeting. Must be at least 1.
    /// </summary>
    public int RecentTurnKeepCount { get; set; } = 4;

    /// <summary>
    ///     Character budget an oversized historical tool result is truncated down to (an explicit omitted-count marker is
    ///     appended) before whole turns are dropped. Zero collapses an oversized historical tool result to just the
    ///     marker.
    /// </summary>
    public int HistoricalToolResultExcerptChars { get; set; } = 2000;

    /// <summary>
    ///     Fallback context-window size (in tokens) used when the package carries no explicit <c>num_ctx</c> override, so
    ///     capacity can still be derived without probing the model. Must be at least 1.
    /// </summary>
    public int DefaultContextTokens { get; set; } = 8192;
}
