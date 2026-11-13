namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

/// <summary>One conversation's searchable text and the list fields a hit carries.</summary>
internal sealed class ConversationSearchDocument
{
    public required string? Title { get; init; }

    public required string Content { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required string Origin { get; init; }

    public required bool IsPinned { get; init; }

    public required bool Archived { get; init; }
}
