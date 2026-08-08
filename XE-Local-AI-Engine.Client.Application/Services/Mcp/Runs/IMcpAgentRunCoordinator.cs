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

public sealed record McpAgentRunStartRequest(
    Guid RequestId,
    string Task,
    McpExecutionBindingRequest Binding,
    Guid? WorkspaceId = null);

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

public sealed record McpAgentRunStartResult(
    McpAgentRunStartKind Kind,
    McpAgentRunView? Run,
    string? FailureCode,
    string DisplayMessage);

public enum McpAgentRunCancelKind
{
    Requested,
    AlreadyRequested,
    AlreadyTerminal,
    NotFound,
    Conflict
}

public sealed record McpAgentRunCancelResult(McpAgentRunCancelKind Kind, McpAgentRunView? Run, string DisplayMessage);

public sealed record McpAgentRunView(
    Guid RequestId,
    McpAgentRunStatus Status,
    long Version,
    McpAgentRunStopReason StopReason,
    string? ModelId,
    Guid? AgentDefinitionId,
    Guid? WorkspaceId,
    string? Result,
    string? DisplayMessage,
    string? FailureCode,
    long CreatedAtUtc,
    long? ClaimedAtUtc,
    long? CompletedAtUtc,
    long? PayloadExpiresAtUtc,
    long? CompactedAtUtc,
    bool PayloadExpired);
