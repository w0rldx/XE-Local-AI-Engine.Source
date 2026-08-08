namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

/// <summary>
///     Paged list request for one agent's execution-log diagnostics. The agent id travels in the route;
///     <see cref="Limit" />/<see cref="Offset" /> page the newest-first result set. Both are optional with sane
///     defaults so a body-less GET returns the first page.
/// </summary>
public sealed class ListAgentExecutionLogsRequest
{
    public Guid AgentDefinitionId { get; init; }

    /// <summary>Page size (newest first). Null/0/negative falls back to the default page in the endpoint.</summary>
    public int? Limit { get; init; }

    /// <summary>Rows to skip from the newest end. Null/negative is treated as 0.</summary>
    public int? Offset { get; init; }
}

/// <summary>
///     Wire projection of an execution-log row. Metadata ONLY — there is no message content to redact.
///     <see cref="ErrorClass" /> is an exception type name only (never the exception message or any transcript text);
///     the store contract guarantees this and the endpoint never widens it.
/// </summary>
public sealed class AgentExecutionLogResponse
{
    public required Guid Id { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? MessageId { get; init; }

    public required string ModelName { get; init; }

    public required string ConfigHash { get; init; }

    public required long LatencyMs { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public required bool Success { get; init; }

    /// <summary>Exception type name only; null on success. Never the exception message or transcript text.</summary>
    public string? ErrorClass { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET agents/{agentDefinitionId}/execution-logs</c>.</summary>
public sealed class ListAgentExecutionLogsResponse
{
    public required IReadOnlyList<AgentExecutionLogResponse> Items { get; init; }
}
