namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped, append-only persistence for agent execution telemetry (adaptive memory diagnostics). Rows hold
///     metadata only — latency/tokens/success/errorClass/configHash plus link ids — and are NEVER encrypted; no message
///     content is stored here. The store owns id/timestamp stamping; it performs no content validation.
/// </summary>
public interface IAgentExecutionLogStore
{
    /// <summary>
    ///     Appends a new adaptive-memory diagnostics row (<see cref="AgentExecutionLogRecordKind.AdaptiveMemoryDiagnostics" />),
    ///     assigning <c>Id</c> and <c>CreatedAtUtc</c>, and returns the stored record.
    /// </summary>
    Task<AgentExecutionLogRecord> AddAsync(AgentExecutionLogInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Idempotent upsert of ONE content-free durable run-envelope row
    ///     (<see cref="AgentExecutionLogRecordKind.ChatRunEnvelope" />) for a terminalized chat invocation, keyed on the
    ///     assistant <c>MessageId</c> (a filtered unique index enforces one envelope per message). A retry or a
    ///     crash-recovery backfill for the same message is a no-op — the first write wins — so it can never duplicate.
    ///     The store assigns <c>Id</c>/<c>CreatedAtUtc</c>, stamps the current envelope schema version, and records
    ///     <c>AgentDefinitionId</c> as <see cref="System.Guid.Empty" /> (the bound agent id is not available at the
    ///     terminalization seam). Metadata only — supply NO message content.
    /// </summary>
    Task AddRunEnvelopeAsync(AgentRunEnvelopeInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Queryable read path for the versioned durable run envelopes (<see cref="AgentExecutionLogRecordKind.ChatRunEnvelope" />),
    ///     newest first, optionally scoped to one conversation. Each row carries its <c>SchemaVersion</c> so a reader can
    ///     tell shapes apart. Metadata only — no message content.
    /// </summary>
    Task<IReadOnlyList<AgentRunEnvelopeRecord>> ListRunEnvelopesAsync(Guid? conversationId, int limit, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns a page of execution logs for <paramref name="agentDefinitionId" />, newest first, capped to
    ///     <paramref name="limit" /> rows and skipping <paramref name="offset" /> rows.
    /// </summary>
    Task<IReadOnlyList<AgentExecutionLogRecord>> ListByAgentAsync(Guid agentDefinitionId, int limit, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retention sweep: deletes every log created before <paramref name="cutoffEpochMs" /> (matched on
    ///     <c>CreatedAtUtc</c>, unix-milliseconds) with a single set-based <c>ExecuteDeleteAsync</c> (no tracking).
    ///     Returns the number of rows deleted.
    /// </summary>
    Task<int> DeleteOlderThanAsync(long cutoffEpochMs, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Per-agent row cap: for every agent, keeps the newest <paramref name="maxPerAgent" /> logs and deletes the rest
    ///     with a single set-based <c>ExecuteDeleteAsync</c> (no tracking). A non-positive cap deletes nothing. Returns the
    ///     number of rows deleted.
    /// </summary>
    Task<int> TrimToMaxPerAgentAsync(int maxPerAgent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Shared constants for the durable run-envelope record so the store writer and the startup recovery backfill stamp
///     the same schema version (single source of truth).
/// </summary>
public static class AgentRunEnvelope
{
    /// <summary>
    ///     Current run-envelope shape version. Bump when the envelope's field set changes so a reader can tell old rows
    ///     apart. v2 (R4): added reasoning/total tokens + started_at_utc lifecycle fields and the deterministic
    ///     message-id upsert key.
    /// </summary>
    public const int CurrentSchemaVersion = 2;
}

/// <summary>
///     Discriminates the producer of an <c>agent_execution_logs</c> row. Retention operates on the whole table, so both
///     kinds are pruned by the same sweep; the discriminator only separates the diagnostics read view from the run ledger.
/// </summary>
public enum AgentExecutionLogRecordKind
{
    /// <summary>Adaptive-memory diagnostics row: one per memory-enabled run, written by the memory extraction worker.</summary>
    AdaptiveMemoryDiagnostics = 0,

    /// <summary>Durable per-invocation run envelope: one content-free row per ordinary chat invocation at terminalization (MED-007).</summary>
    ChatRunEnvelope = 1
}

/// <summary>
///     Fields for a durable run-envelope row (<see cref="AgentExecutionLogRecordKind.ChatRunEnvelope" />). Bounded and
///     content-free: correlation ids, terminal status, usage/timing counters and a trace id only — NEVER prompt, model
///     output, or tool arguments. <see cref="FailureCategory" /> is a category enum name (e.g. <c>ProviderUnreachable</c>),
///     never an exception message or transcript text. Nullable fields are omitted when not reachable at the seam.
/// </summary>
public sealed record AgentRunEnvelopeInput(
    Guid? ConversationId,
    Guid? MessageId,
    Guid? InvocationId,
    Guid? RequestId,
    string ModelName,
    string TerminalStatus,
    bool Success,
    long DurationMs,
    string? FailureCategory = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    int? ContentChunkCount = null,
    int? ReasoningChunkCount = null,
    string? TraceId = null,
    int? ReasoningTokens = null,
    int? TotalTokens = null,
    long? StartedAtUtc = null);

/// <summary>
///     Versioned read projection of a durable run-envelope row (always
///     <see cref="AgentExecutionLogRecordKind.ChatRunEnvelope" />). Metadata only — no message content;
///     <see cref="FailureCategory" /> is a category enum name only. <see cref="SchemaVersion" /> lets a reader tell
///     envelope shapes apart as the field set evolves.
/// </summary>
public sealed record AgentRunEnvelopeRecord(
    Guid Id,
    int SchemaVersion,
    Guid? ConversationId,
    Guid? MessageId,
    Guid? InvocationId,
    Guid? RequestId,
    string ModelName,
    string TerminalStatus,
    bool Success,
    string? FailureCategory,
    long DurationMs,
    int? PromptTokens,
    int? CompletionTokens,
    int? ReasoningTokens,
    int? TotalTokens,
    int? ContentChunkCount,
    int? ReasoningChunkCount,
    string? TraceId,
    long? StartedAtUtc,
    long CreatedAtUtc);

/// <summary>
///     Typed projection of a persisted execution-log row. Metadata only — no message content. <see cref="ErrorClass" />
///     is an exception type name only (never the message text).
/// </summary>
public sealed record AgentExecutionLogRecord(
    Guid Id,
    Guid AgentDefinitionId,
    Guid? ConversationId,
    Guid? MessageId,
    string ModelName,
    string ConfigHash,
    long LatencyMs,
    int? PromptTokens,
    int? CompletionTokens,
    bool Success,
    string? ErrorClass,
    long CreatedAtUtc);

/// <summary>
///     Fields supplied when appending an execution-log row. Metadata only — supply NO message content here.
///     <see cref="ErrorClass" /> must be an exception type name only, never the exception message or transcript text.
/// </summary>
public sealed record AgentExecutionLogInput(
    Guid AgentDefinitionId,
    Guid? ConversationId,
    Guid? MessageId,
    string ModelName,
    string ConfigHash,
    long LatencyMs,
    bool Success,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? ErrorClass = null);
