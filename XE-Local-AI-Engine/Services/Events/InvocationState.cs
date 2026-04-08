namespace XE_Local_AI_Engine.Services.Events;

using XE_Local_AI_Engine.Models.Enums;

public sealed class InvocationState
{
    public Guid InvocationId { get; init; }

    public Guid ConversationId { get; init; }

    public InvocationStatus Status { get; set; }

    public string StreamedContent { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public string? Error { get; set; }

    public string? ModelUsed { get; set; }
}
