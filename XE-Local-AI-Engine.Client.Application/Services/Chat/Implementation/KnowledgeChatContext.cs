namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>
///     The composed knowledge-base context for one plain-chat turn: the fenced prompt block and the ordered provenance
///     of the hits inlined into it (the render source for the "Sources" strip).
/// </summary>
internal sealed class KnowledgeChatContext
{
    public required string Context { get; init; }

    public required IReadOnlyList<NodeChatMessageSource> Sources { get; init; }
}
