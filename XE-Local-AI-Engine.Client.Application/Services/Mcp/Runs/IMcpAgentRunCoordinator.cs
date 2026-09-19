namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>Application boundary used by MCP tools to accept and inspect durable unattended runs.</summary>
public interface IMcpAgentRunCoordinator
{
    Task<McpAgentRunStartResult> StartAsync(McpAgentRunStartRequest request, CancellationToken cancellationToken);

    Task<McpAgentRunView?> GetAsync(Guid requestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<McpAgentRunView>> ListAsync(int? limit,
        McpAgentRunStatus? status,
        CancellationToken cancellationToken);

    Task<McpAgentRunCancelResult> CancelAsync(Guid requestId, CancellationToken cancellationToken);
}

public sealed record McpAgentRunStartRequest
{
    public required Guid RequestId { get; init; }

    public required string Task { get; init; }

    public required McpExecutionBindingRequest Binding { get; init; }

    public Guid? WorkspaceId { get; init; }
}

public static class McpAgentRunFailureCodes
{
    public const string WorkspaceNotAuthorized = McpExecutionFailureCodes.WorkspaceNotAuthorized;
}

public enum McpAgentRunStartKind
{
    Accepted,
    Existing,
    ResultExpired,
    RequestIdConflict,
    CapacityExceeded,
    Rejected
}

public sealed class McpAgentRunStartResult
{
    public required McpAgentRunStartKind Kind { get; init; }

    public required McpAgentRunView? Run { get; init; }

    public required string? FailureCode { get; init; }

    public required string DisplayMessage { get; init; }
}

public enum McpAgentRunCancelKind
{
    Requested,
    AlreadyRequested,
    AlreadyTerminal,
    NotFound,
    Conflict
}

public sealed class McpAgentRunCancelResult
{
    public required McpAgentRunCancelKind Kind { get; init; }

    public required McpAgentRunView? Run { get; init; }

    public required string DisplayMessage { get; init; }
}

public sealed record McpAgentRunView
{
    public required Guid RequestId { get; init; }

    public required McpAgentRunStatus Status { get; init; }

    public required long Version { get; init; }

    public required McpAgentRunStopReason StopReason { get; init; }

    public required string? ModelId { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required Guid? WorkspaceId { get; init; }

    public required string? Result { get; init; }

    public required string? DisplayMessage { get; init; }

    public required string? FailureCode { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long? ClaimedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long? PayloadExpiresAtUtc { get; init; }

    public required long? CompactedAtUtc { get; init; }

    public required bool PayloadExpired { get; init; }
}
