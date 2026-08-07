namespace XE_Local_AI_Engine.Client.Services.Mcp.Server;

using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;

/// <summary>Bounded, path-free workspace discovery response for external MCP clients.</summary>
public sealed record McpWorkspaceListResponse(
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("workspaces")]
    IReadOnlyList<McpWorkspaceSummary> Workspaces,
    [property: JsonPropertyName("count")]
    int Count,
    [property: JsonPropertyName("truncated")]
    bool Truncated,
    [property: JsonPropertyName("failure_code")]
    string? FailureCode = null,
    [property: JsonPropertyName("display_message")]
    string? DisplayMessage = null);

/// <summary>An opaque read-only workspace reference. A host path is intentionally not representable.</summary>
public sealed record McpWorkspaceSummary(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("alias")]
    string Alias,
    [property: JsonPropertyName("mode")]
    string Mode);

/// <summary>Immediate admission response for a durable background run.</summary>
public sealed record McpAgentRunStartResponse(
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("run")]
    McpAgentRunSummary? Run,
    [property: JsonPropertyName("failure_code")]
    string? FailureCode,
    [property: JsonPropertyName("display_message")]
    string DisplayMessage);

/// <summary>Bounded poll response for one durable background run.</summary>
public sealed record McpAgentRunGetResponse(
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("run")]
    McpAgentRunDetail? Run,
    [property: JsonPropertyName("failure_code")]
    string? FailureCode,
    [property: JsonPropertyName("display_message")]
    string DisplayMessage);

/// <summary>Structured cancellation response; expected lifecycle races are represented as ordinary values.</summary>
public sealed record McpAgentRunCancelResponse(
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("run")]
    McpAgentRunSummary? Run,
    [property: JsonPropertyName("failure_code")]
    string? FailureCode,
    [property: JsonPropertyName("display_message")]
    string DisplayMessage);

/// <summary>Bounded run listing that never contains task, instructions, result content, or a host path.</summary>
public sealed record McpAgentRunListResponse(
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("runs")]
    IReadOnlyList<McpAgentRunSummary> Runs,
    [property: JsonPropertyName("count")]
    int Count,
    [property: JsonPropertyName("limit")]
    int Limit,
    [property: JsonPropertyName("failure_code")]
    string? FailureCode = null,
    [property: JsonPropertyName("display_message")]
    string? DisplayMessage = null);

/// <summary>Content-free lifecycle metadata safe to return from start, cancel, and list operations.</summary>
public sealed record McpAgentRunSummary(
    [property: JsonPropertyName("request_id")]
    string RequestId,
    [property: JsonPropertyName("status")]
    string Status,
    [property: JsonPropertyName("version")]
    long Version,
    [property: JsonPropertyName("stop_reason")]
    string StopReason,
    [property: JsonPropertyName("model_id")]
    string? ModelId,
    [property: JsonPropertyName("agent_definition_id")]
    string? AgentDefinitionId,
    [property: JsonPropertyName("workspace_id")]
    string? WorkspaceId,
    [property: JsonPropertyName("failure_code")]
    string? FailureCode,
    [property: JsonPropertyName("display_message")]
    string? DisplayMessage,
    [property: JsonPropertyName("created_at_unix_ms")]
    long CreatedAtUnixMilliseconds,
    [property: JsonPropertyName("claimed_at_unix_ms")]
    long? ClaimedAtUnixMilliseconds,
    [property: JsonPropertyName("completed_at_unix_ms")]
    long? CompletedAtUnixMilliseconds,
    [property: JsonPropertyName("payload_expires_at_unix_ms")]
    long? PayloadExpiresAtUnixMilliseconds,
    [property: JsonPropertyName("compacted_at_unix_ms")]
    long? CompactedAtUnixMilliseconds,
    [property: JsonPropertyName("result_expired")]
    bool ResultExpired,
    [property: JsonPropertyName("compacted")]
    bool Compacted);

/// <summary>One bounded result poll. <see cref="ResultTruncated" /> is true whenever result content was clipped.</summary>
public sealed record McpAgentRunDetail(
    [property: JsonPropertyName("metadata")]
    McpAgentRunSummary Metadata,
    [property: JsonPropertyName("result")]
    string? Result,
    [property: JsonPropertyName("result_truncated")]
    bool ResultTruncated);

internal static class McpAgentToolResponseMapper
{
    public static McpAgentRunSummary ToSummary(McpAgentRunView run) =>
        new(run.RequestId.ToString("D"),
            ToExternalValue(run.Status),
            run.Version,
            ToExternalValue(run.StopReason),
            run.ModelId,
            run.AgentDefinitionId?.ToString("D"),
            run.WorkspaceId?.ToString("D"),
            run.FailureCode,
            run.DisplayMessage,
            run.CreatedAtUtc,
            run.ClaimedAtUtc,
            run.CompletedAtUtc,
            run.PayloadExpiresAtUtc,
            run.CompactedAtUtc,
            run.PayloadExpired,
            run.CompactedAtUtc.HasValue);

    public static string ToExternalValue<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        McpAgentRunText.ToLowercaseInvariant(value);
}
