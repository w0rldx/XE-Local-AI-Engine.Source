namespace XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

/// <summary>One delta item the reducer refused, reported so the caller can log it.</summary>
public sealed class ConversationStateRejection
{
    /// <summary><c>add</c>, <c>supersede</c> or <c>resolve</c>.</summary>
    public required string Operation { get; init; }

    public string? EntryId { get; init; }

    public required string Reason { get; init; }
}

public sealed class ConversationStateReduceResult
{
    public required ConversationStateDocument Document { get; init; }

    public IReadOnlyList<ConversationStateRejection> Rejections { get; init; } = [];

    /// <summary>Ids the budget policy dropped after the delta was applied.</summary>
    public IReadOnlyList<string> DroppedIds { get; init; } = [];
}
