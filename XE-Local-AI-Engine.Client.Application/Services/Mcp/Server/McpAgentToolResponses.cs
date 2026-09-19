namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;

/// <summary>Bounded, path-free workspace discovery response for external MCP clients.</summary>
public sealed class McpWorkspaceListResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("workspaces")]
    public required IReadOnlyList<McpWorkspaceSummary> Workspaces { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }

    [JsonPropertyName("display_message")]
    public string? DisplayMessage { get; init; }
}

/// <summary>An opaque read-only workspace reference. A host path is intentionally not representable.</summary>
public sealed class McpWorkspaceSummary
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("alias")]
    public required string Alias { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }
}

/// <summary>Immediate admission response for a durable background run.</summary>
public sealed class McpAgentRunStartResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("run")]
    public required McpAgentRunSummary? Run { get; init; }

    [JsonPropertyName("failure_code")]
    public required string? FailureCode { get; init; }

    [JsonPropertyName("display_message")]
    public required string DisplayMessage { get; init; }
}

/// <summary>Bounded poll response for one durable background run.</summary>
public sealed class McpAgentRunGetResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("run")]
    public required McpAgentRunDetail? Run { get; init; }

    [JsonPropertyName("failure_code")]
    public required string? FailureCode { get; init; }

    [JsonPropertyName("display_message")]
    public required string DisplayMessage { get; init; }
}

/// <summary>Structured cancellation response; expected lifecycle races are represented as ordinary values.</summary>
public sealed class McpAgentRunCancelResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("run")]
    public required McpAgentRunSummary? Run { get; init; }

    [JsonPropertyName("failure_code")]
    public required string? FailureCode { get; init; }

    [JsonPropertyName("display_message")]
    public required string DisplayMessage { get; init; }
}

/// <summary>Bounded run listing that never contains task, instructions, result content, or a host path.</summary>
public sealed class McpAgentRunListResponse
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("runs")]
    public required IReadOnlyList<McpAgentRunSummary> Runs { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    [JsonPropertyName("failure_code")]
    public string? FailureCode { get; init; }

    [JsonPropertyName("display_message")]
    public string? DisplayMessage { get; init; }
}

/// <summary>Content-free lifecycle metadata safe to return from start, cancel, and list operations.</summary>
public sealed class McpAgentRunSummary
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("stop_reason")]
    public required string StopReason { get; init; }

    [JsonPropertyName("model_id")]
    public required string? ModelId { get; init; }

    [JsonPropertyName("agent_definition_id")]
    public required string? AgentDefinitionId { get; init; }

    [JsonPropertyName("workspace_id")]
    public required string? WorkspaceId { get; init; }

    [JsonPropertyName("failure_code")]
    public required string? FailureCode { get; init; }

    [JsonPropertyName("display_message")]
    public required string? DisplayMessage { get; init; }

    [JsonPropertyName("created_at_unix_ms")]
    public required long CreatedAtUnixMilliseconds { get; init; }

    [JsonPropertyName("claimed_at_unix_ms")]
    public required long? ClaimedAtUnixMilliseconds { get; init; }

    [JsonPropertyName("completed_at_unix_ms")]
    public required long? CompletedAtUnixMilliseconds { get; init; }

    [JsonPropertyName("payload_expires_at_unix_ms")]
    public required long? PayloadExpiresAtUnixMilliseconds { get; init; }

    [JsonPropertyName("compacted_at_unix_ms")]
    public required long? CompactedAtUnixMilliseconds { get; init; }

    [JsonPropertyName("result_expired")]
    public required bool ResultExpired { get; init; }

    [JsonPropertyName("compacted")]
    public required bool Compacted { get; init; }
}

/// <summary>One bounded result poll. <see cref="ResultTruncated" /> is true whenever result content was clipped.</summary>
public sealed class McpAgentRunDetail
{
    [JsonPropertyName("metadata")]
    public required McpAgentRunSummary Metadata { get; init; }

    [JsonPropertyName("result")]
    public required string? Result { get; init; }

    [JsonPropertyName("result_truncated")]
    public required bool ResultTruncated { get; init; }
}

internal static class McpAgentToolResponseMapper
{
    public static McpAgentRunSummary ToSummary(McpAgentRunView run) =>
        new()
        {
            RequestId = run.RequestId.ToString("D"),
            Status = ToExternalValue(run.Status),
            Version = run.Version,
            StopReason = ToExternalValue(run.StopReason),
            ModelId = run.ModelId,
            AgentDefinitionId = run.AgentDefinitionId?.ToString("D"),
            WorkspaceId = run.WorkspaceId?.ToString("D"),
            FailureCode = run.FailureCode,
            DisplayMessage = run.DisplayMessage,
            CreatedAtUnixMilliseconds = run.CreatedAtUtc,
            ClaimedAtUnixMilliseconds = run.ClaimedAtUtc,
            CompletedAtUnixMilliseconds = run.CompletedAtUtc,
            PayloadExpiresAtUnixMilliseconds = run.PayloadExpiresAtUtc,
            CompactedAtUnixMilliseconds = run.CompactedAtUtc,
            ResultExpired = run.PayloadExpired,
            Compacted = run.CompactedAtUtc.HasValue
        };

    public static string ToExternalValue<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        McpAgentRunText.ToLowercaseInvariant(value);
}
