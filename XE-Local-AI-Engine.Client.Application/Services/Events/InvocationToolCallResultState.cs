namespace XE_Local_AI_Engine.Client.Services.Events;

public sealed class InvocationToolCallResultState
{
    public required string RequestId { get; init; }

    public required bool Succeeded { get; init; }

    public required string Result { get; init; }

    public required string? Error { get; init; }

    public required DateTimeOffset ResolvedAt { get; init; }
}
