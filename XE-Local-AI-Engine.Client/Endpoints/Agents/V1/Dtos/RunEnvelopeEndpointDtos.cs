namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

/// <summary>
///     Paged list request for the durable run-envelope lifecycle records. Optional <see cref="ConversationId" /> scopes
///     to one conversation; <see cref="Limit" />/<see cref="Offset" /> page the newest-first result set with sane defaults.
/// </summary>
public sealed class ListRunEnvelopesRequest
{
    /// <summary>Optional conversation filter. Null returns run envelopes across all conversations.</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>Page size (newest first). Null/0/negative falls back to the default page in the endpoint.</summary>
    public int? Limit { get; init; }

    /// <summary>Rows to skip from the newest end. Null/negative is treated as 0.</summary>
    public int? Offset { get; init; }
}

/// <summary>
///     Wire projection of a durable run-envelope lifecycle record. Metadata ONLY — there is no message content to redact.
///     <see cref="SchemaVersion" /> versions the shape; <see cref="FailureCategory" /> is a category enum name only
///     (never an exception message or transcript text).
/// </summary>
public sealed class AgentRunEnvelopeResponse
{
    public required Guid Id { get; init; }

    public required int SchemaVersion { get; init; }

    /// <summary>Bound agent definition id when the run executed under one; <see cref="System.Guid.Empty" /> otherwise.</summary>
    public required Guid AgentDefinitionId { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? MessageId { get; init; }

    public Guid? InvocationId { get; init; }

    public Guid? RequestId { get; init; }

    public required string ModelName { get; init; }

    public required string TerminalStatus { get; init; }

    public required bool Success { get; init; }

    /// <summary>Failure-category enum name only; null on success. Never an exception message or transcript text.</summary>
    public string? FailureCategory { get; init; }

    public required long DurationMs { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public int? TotalTokens { get; init; }

    /// <summary>
    ///     Estimated tool-schema tokens the turn spent, cumulative across its provider rounds; null on a row written
    ///     before this field existed or by the restart-recovery backfill. A <c>long</c>, unlike the token members above,
    ///     because its source counter is one.
    /// </summary>
    public long? ToolSchemaTokens { get; init; }

    /// <summary>The largest single round's estimated tool-schema token count; null for the same reasons.</summary>
    public int? MaxToolSchemaTokens { get; init; }

    public int? ContentChunkCount { get; init; }

    public int? ReasoningChunkCount { get; init; }

    public string? TraceId { get; init; }

    public long? StartedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET agents/run-envelopes</c>.</summary>
public sealed class ListRunEnvelopesResponse
{
    public required IReadOnlyList<AgentRunEnvelopeResponse> Items { get; init; }
}
