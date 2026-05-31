namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using Microsoft.Extensions.AI;

/// <summary>
///     Provider-agnostic description of the single agent needed to run one local or platform invocation.
/// </summary>
/// <param name="ModelId">Model id passed to the underlying <see cref="IChatClient" /> for this turn.</param>
/// <param name="Instructions">System instructions prepended to <paramref name="ConversationContext" />.</param>
/// <param name="Tools">Offer-list tools projected from the runtime package before executable registry resolution.</param>
/// <param name="ConversationContext">Prior conversation turns that should seed the agent run.</param>
/// <param name="ReasoningEffort">Optional reasoning budget hint mapped to provider-specific chat options.</param>
public sealed record InvocationAgentDefinition(
    string ModelId,
    string Instructions,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<ChatMessage> ConversationContext,
    string? ReasoningEffort = null);
