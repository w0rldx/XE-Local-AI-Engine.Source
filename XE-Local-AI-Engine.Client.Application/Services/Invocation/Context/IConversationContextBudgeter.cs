namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;

/// <summary>
///     Deterministically fits a conversation history into an input-token budget before it is sent to the provider.
///     Policy: always keep system messages, the latest user message, and the most recent turns; when the estimated
///     footprint exceeds the budget, first shorten oversized historical tool results to an excerpt, then drop the oldest
///     turns whole (never splitting an assistant tool-call from its tool-result). The most recent turns are never
///     modified, so an in-flight tool-calling round is preserved intact. No LLM summarization is performed.
/// </summary>
public interface IConversationContextBudgeter
{
    /// <summary>
    ///     Produces a budgeted copy of <paramref name="messages" /> that fits within
    ///     <paramref name="contextTokenCapacity" /> minus <paramref name="reservedOutputTokens" /> minus the fixed
    ///     per-round input overhead of <paramref name="systemPrompt" /> and <paramref name="toolDefinitions" />. Returns
    ///     the input unchanged (reference-equal) when it already fits. When even the always-keep set exceeds the budget it
    ///     is kept anyway (the caller's per-message validator bounds individual message size) and the result is flagged
    ///     trimmed.
    /// </summary>
    /// <param name="messages">The ordered history to budget.</param>
    /// <param name="contextTokenCapacity">The model's effective context window in tokens.</param>
    /// <param name="reservedOutputTokens">Tokens to hold back for the model's response.</param>
    /// <param name="systemPrompt">
    ///     ORC-02: the resolved system prompt that is prepended to the request AFTER this history but still counts against
    ///     the window. Estimated (as a System message) and folded into the effective budget so the outer budget/hard-stop
    ///     is measured against the true round, not history alone. <see langword="null" /> counts as no system prompt.
    /// </param>
    /// <param name="toolDefinitions">
    ///     ORC-02: the model-facing definition text (name + description + parameter schema) of each tool advertised on the
    ///     request. Tool JSON schemas never appear in <paramref name="messages" /> yet count against the window, so each
    ///     entry is estimated as one framed unit and folded into the effective budget — mirroring how the inner
    ///     <c>ProviderCallBudgetChatClient</c> folds its Instructions + Tools overhead so the two approximately agree
    ///     (the outer estimate frames each tool as its own System message, so it over-counts slightly — the safe
    ///     direction, trimming a touch early rather than rejecting late).
    ///     <see langword="null" />/empty counts as no tools.
    /// </param>
    ConversationBudgetResult Budget(IReadOnlyList<ChatMessage> messages,
        int contextTokenCapacity,
        int reservedOutputTokens,
        string? systemPrompt = null,
        IReadOnlyList<string>? toolDefinitions = null);
}
