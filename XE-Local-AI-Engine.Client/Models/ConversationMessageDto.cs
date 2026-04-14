namespace XE_Local_AI_Engine.Client.Models;

using XE_Local_AI_Engine.Client.Models.Enums;

public sealed record ConversationMessageDto
{
    public required Guid Id { get; init; }

    public required MessageRole Role { get; init; }

    public required string Content { get; init; }

    public string? ToolCalls { get; init; }

    public string? ToolResults { get; init; }

    public string? ModelUsed { get; init; }

    public required int SortOrder { get; init; }
}