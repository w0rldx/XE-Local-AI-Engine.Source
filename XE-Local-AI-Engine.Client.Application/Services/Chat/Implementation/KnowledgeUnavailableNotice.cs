namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>The send and regenerate paths' one notice for a grounded turn on a node with no embedding model (model-matrix F13).</summary>
internal static class KnowledgeUnavailableNotice
{
    // The SPA localizes this exact sentence; change both together.
    internal const string Message =
        "Your knowledge base was not used for this message because no embedding model is installed, so your documents could not be indexed.";

    public static TurnNoticePayload For(Guid requestId) =>
        new()
        {
            InvocationId = requestId,
            Kind = TurnNoticeKind.KnowledgeUnavailable,
            Message = Message
        };
}
