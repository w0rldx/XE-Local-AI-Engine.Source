namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using Microsoft.Extensions.AI;

public sealed record InvocationAgentDefinition(
    string ModelId,
    string Instructions,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<ChatMessage> ConversationContext);
