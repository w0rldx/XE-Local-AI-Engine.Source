namespace XE_Local_AI_Engine.Client.Models;

public sealed record InvocationAssignedEvent
{
    public required RuntimePackage RuntimePackage { get; init; }
}

public sealed record ToolCallResultEvent
{
    public required string RequestId { get; init; }

    public required string Result { get; init; }

    public string? Error { get; init; }
}

public sealed record DisconnectRequestedEvent
{
    public required string Reason { get; init; }
}

public sealed record ApprovalResolvedEvent
{
    public required string RequestId { get; init; }

    public required bool Approved { get; init; }
}

public sealed record InvocationCancelledEvent
{
    public required Guid InvocationId { get; init; }

    public required string Reason { get; init; }
}

public sealed record WorkerHelloPayload
{
    public required Guid ClientNodeId { get; init; }
}

public sealed record TokenStreamChunkPayload
{
    public required Guid InvocationId { get; init; }

    public required string Token { get; init; }

    public required bool IsComplete { get; init; }
}

public sealed record ToolCallRequestPayload
{
    public required Guid InvocationId { get; init; }

    public required string RequestId { get; init; }

    public required string ToolName { get; init; }

    public required string Parameters { get; init; }
}

public sealed record ApprovalRequestPayload
{
    public required Guid InvocationId { get; init; }

    public required string RequestId { get; init; }

    public required string Description { get; init; }
}

public sealed record InvocationCompletedPayload
{
    public required Guid InvocationId { get; init; }

    public required string FinalContent { get; init; }

    public string? ModelUsed { get; init; }

    public int? TokensUsed { get; init; }
}

public sealed record InvocationFailedPayload
{
    public required Guid InvocationId { get; init; }

    public required string Error { get; init; }

    public string? FailureCategory { get; init; }
}

public sealed record HeartbeatPayload
{
    public required Guid ClientNodeId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }
}
