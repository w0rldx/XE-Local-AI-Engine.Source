namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

/// <summary>Projects a persisted chat message onto the distiller's input, keeping tool calls as short excerpts.</summary>
internal static class ConversationStateSourceMessageMapper
{
    /// <summary>Per-excerpt cap for a tool call's arguments and its result.</summary>
    internal const int ToolExcerptChars = 400;

    public static ConversationStateSourceMessage Map(NodeChatPersistedMessageDto message, int anchorSequence)
    {
        ArgumentNullException.ThrowIfNull(message);

        // A tool part carries its call AND its collapsed result (NodeChatPartAccumulator); a legacy message has no parts.
        var tools = (message.Parts ?? [])
                    .Where(static part => string.Equals(part.Kind, NodeChatMessagePartKinds.Tool, StringComparison.Ordinal)
                                          && !string.IsNullOrWhiteSpace(part.Name))
                    .OrderBy(static part => part.Sequence)
                    .Select(static part => new ConversationStateToolPart
                    {
                        Name = part.Name!,
                        ArgumentsExcerpt = Excerpt(part.Args),
                        ResultExcerpt = Excerpt(part.Result)
                    })
                    .ToList();

        return new ConversationStateSourceMessage
        {
            Sequence = anchorSequence,
            Role = message.Role,
            Content = message.Content,
            Tools = tools
        };
    }

    internal static string? Excerpt(string? value) =>
        value is null ? null : ConversationSummarizer.TruncateAtRuneBoundary(value, ToolExcerptChars);
}
