namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;

/// <summary>
///     Deterministically fits a conversation history into an input-token budget before it is sent to the provider.
/// </summary>
/// <remarks>
///     System messages, the latest user message and the most recent turns are always kept, so an in-flight
///     tool-calling round survives; over budget, oversized historical tool results are excerpted, then the oldest turns
///     dropped whole — never splitting a tool call from its result — then whole historical approval groups evicted.
///     Two opt-in last-resort passes reach into the protected window only when it alone exceeds the budget, touching no
///     call, correlation id or approval record. No LLM summarization is performed at any point.
/// </remarks>
public interface IConversationContextBudgeter
{
    /// <summary>
    ///     Produces a budgeted copy of <paramref name="messages" /> fitting the capacity left after the reserved output tokens and the fixed per-round overhead.
    /// </summary>
    /// <remarks>
    ///     Returns the input unchanged and reference-equal when it already fits; an always-keep set that alone exceeds the budget is kept anyway and flagged
    ///     trimmed, since the caller's per-message validator bounds individual message size. Each tool definition, and the system prompt unless the history
    ///     already carries it, count against the window without being droppable, so each is folded in as one framed unit — mirroring the inner budgeter,
    ///     over-counting slightly, the safe direction. The prompt is counted exactly once either way.
    /// </remarks>
    /// <param name="messages">The ordered history to budget.</param>
    /// <param name="contextTokenCapacity">The model's effective context window in tokens.</param>
    /// <param name="reservedOutputTokens">Tokens to hold back for the model's response.</param>
    /// <param name="systemPrompt">The resolved system prompt, overhead unless a System message in the history carries it; <see langword="null" /> is none.</param>
    /// <param name="toolDefinitions">Each advertised tool's model-facing name, description and parameter schema; <see langword="null" /> or empty counts as none.</param>
    /// <param name="modelName">Resolved provider model identity used only to select an existing token calibration.</param>
    ConversationBudgetResult Budget(IReadOnlyList<ChatMessage> messages,
        int contextTokenCapacity,
        int reservedOutputTokens,
        string? systemPrompt = null,
        IReadOnlyList<string>? toolDefinitions = null,
        string? modelName = null);
}
