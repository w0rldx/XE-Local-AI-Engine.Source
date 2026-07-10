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
    ///     <paramref name="contextTokenCapacity" /> minus <paramref name="reservedOutputTokens" />. Returns the input
    ///     unchanged (reference-equal) when it already fits. When even the always-keep set exceeds the budget it is kept
    ///     anyway (the caller's per-message validator bounds individual message size) and the result is flagged trimmed.
    /// </summary>
    /// <param name="messages">The ordered history to budget.</param>
    /// <param name="contextTokenCapacity">The model's effective context window in tokens.</param>
    /// <param name="reservedOutputTokens">Tokens to hold back for the model's response.</param>
    ConversationBudgetResult Budget(IReadOnlyList<ChatMessage> messages, int contextTokenCapacity, int reservedOutputTokens);
}
