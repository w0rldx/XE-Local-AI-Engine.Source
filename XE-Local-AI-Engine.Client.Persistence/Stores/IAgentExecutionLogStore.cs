namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped, append-only persistence for agent execution telemetry. FOUR producers share this table, each
///     discriminated by <see cref="AgentExecutionLogRecordKind" />: adaptive-memory diagnostics, the durable chat
///     run envelope, the tool-approval decision audit and the integration invocation audit. Rows hold metadata only —
///     latency/tokens/success/errorClass/configHash plus link ids — and are NEVER encrypted; no message content is
///     stored here. Column meanings are overloaded across kinds, so <b>every read and aggregate must filter by
///     <c>record_kind</c></b>. The store owns id/timestamp stamping; it performs no content validation.
/// </summary>
public interface IAgentExecutionLogStore
{
    /// <summary>
    ///     Appends a new adaptive-memory diagnostics row (<see cref="AgentExecutionLogRecordKind.AdaptiveMemoryDiagnostics" />),
    ///     assigning <c>Id</c> and <c>CreatedAtUtc</c>, and returns the stored record.
    /// </summary>
    Task<AgentExecutionLogRecord> AddAsync(AgentExecutionLogInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends a single integration-invocation audit row (<see cref="AgentExecutionLogRecordKind.IntegrationInvocation" />),
    ///     assigning <c>Id</c> and <c>CreatedAtUtc</c>. Metadata only — no inputs, no outputs, no message content,
    ///     never encrypted. <c>ConversationId</c> stays null even though every execution owns a conversation, so a
    ///     conversation purge does not reach these rows; they age out with the execution-log retention sweep instead.
    /// </summary>
    Task AddIntegrationInvocationAsync(IntegrationInvocationAuditInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends a single tool-approval DECISION audit row (<see cref="AgentExecutionLogRecordKind.ApprovalDecision" />),
    ///     assigning <c>Id</c> and <c>CreatedAtUtc</c>. Metadata only — no message content, never encrypted: the
    ///     tool name, the resolved decision (approve / deny / timeout), the decision source (local / hub), and the tool's
    ///     risk category are all non-sensitive category labels, reused across existing columns without a schema change.
    ///     Agentic MCP decisions use the bounded <c>mcp-agentic:&lt;key-prefix&gt;</c> source convention.
    /// </summary>
    Task AddApprovalDecisionAsync(ApprovalDecisionAuditInput input, CancellationToken cancellationToken = default);

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

    /// <summary>
    ///     Aggregates token usage over the durable run envelopes
    ///     (<see cref="AgentExecutionLogRecordKind.ChatRunEnvelope" /> only — the per-invocation usage ledger), grouped by
    ///     model name, fine-grained <see cref="AgentUsageProviders">provider</see>, and UTC day, summed with a single
    ///     set-based GROUP BY (no tracking). Adaptive-memory diagnostics rows (kind 0) are excluded — they are a separate
    ///     producer with an incomplete token set (no reasoning/total). The optional half-open range bounds the scan on
    ///     <c>CreatedAtUtc</c> (unix-ms): <paramref name="fromEpochMsInclusive" /> lower-inclusive,
    ///     <paramref name="toEpochMsExclusive" /> upper-exclusive; either may be null for an open end. Buckets are ordered
    ///     newest day first, then provider, then model name. The grand totals and the per-provider rollup are folded from
    ///     these buckets by the caller (the mapper). Metadata only — no message content. NOTE: the retention sweep ages
    ///     rows out (see <c>AgentExecutionLogRetentionOptions</c>), so this only covers the retained horizon.
    /// </summary>
    Task<IReadOnlyList<TokenUsageAggregateRecord>> SummarizeTokenUsageAsync(long? fromEpochMsInclusive,
        long? toEpochMsExclusive,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Canonical, lowercase values of the fine-grained runtime-provider dimension carried on run-envelope rows and
///     surfaced by the usage summary. Non-sensitive category labels (stored plaintext like the model name). The write
///     path classifies the turn's runtime into one of these; <see cref="Unknown" /> is the backfill default for rows that
///     predate the dimension and the fallback when a turn's provider cannot be resolved.
/// </summary>
public static class AgentUsageProviders
{
    /// <summary>The default local runtime (llama.cpp / llama-server).</summary>
    public const string Local = "local";

    /// <summary>The gated secondary local runtime (Ollama).</summary>
    public const string Ollama = "ollama";

    /// <summary>The Codex (ChatGPT OAuth) cloud provider.</summary>
    public const string Codex = "codex";

    /// <summary>The Azure AI Foundry cloud provider.</summary>
    public const string Azure = "azure";

    /// <summary>Backfill default / unresolved-provider fallback.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
///     Canonical, lowercase values of the tool-approval DECISION dimension carried on approval-decision audit rows.
///     Non-sensitive category labels (stored plaintext) — never message content. Also the metric-tag values for
///     the <c>decision</c> dimension so the row and the counter agree on one vocabulary.
/// </summary>
public static class ApprovalDecisions
{
    /// <summary>The operator approved the tool call.</summary>
    public const string Approve = "approve";

    /// <summary>The operator denied the tool call.</summary>
    public const string Deny = "deny";

    /// <summary>No decision arrived before the pending-approval age elapsed; the turn fails as before.</summary>
    public const string Timeout = "timeout";
}

/// <summary>
///     Canonical, lowercase values of the tool-approval SOURCE dimension carried on approval-decision audit rows:
///     where the decision was resolved. Agentic MCP auto-approval uses the dynamic
///     <c>mcp-agentic:&lt;prefix&gt;</c> convention, where the bounded prefix contains only ASCII letters, digits,
///     underscore, or hyphen. Non-sensitive category labels (stored plaintext) — never message content.
/// </summary>
public static class ApprovalDecisionSources
{
    /// <summary>Resolved on the loopback (desktop/local) approval endpoint — no worker hub in the round-trip.</summary>
    public const string Local = "local";

    /// <summary>Resolved through the platform worker hub.</summary>
    public const string Hub = "hub";
}

/// <summary>
///     Shared constants for the durable run-envelope record so the store writer and the startup recovery backfill stamp
///     the same schema version (single source of truth).
/// </summary>
public static class AgentRunEnvelope
{
    /// <summary>
    ///     Current run-envelope shape version. Bump when the envelope's field set changes so a reader can tell old rows
    ///     apart. v2: added reasoning/total tokens + started_at_utc lifecycle fields and the deterministic
    ///     message-id upsert key. v3: written atomically inside the terminalize transaction with the bound
    ///     agent id populated from the winning message row.
    ///     v4: added tool_schema_tokens / max_tool_schema_tokens — the per-turn tool-schema token estimate. Nullable;
    ///     null on rows written by the restart-recovery backfill, which supplies no generation detail.
    /// </summary>
    public const int CurrentSchemaVersion = 4;
}

/// <summary>
///     Discriminates the producer of an <c>agent_execution_logs</c> row. Retention operates on the whole table, so all
///     four kinds are pruned by the same sweep; the discriminator only separates each read view from the others.
/// </summary>
public enum AgentExecutionLogRecordKind
{
    /// <summary>Adaptive-memory diagnostics row: one per memory-enabled run, written by the memory extraction worker.</summary>
    AdaptiveMemoryDiagnostics = 0,

    /// <summary>Durable per-invocation run envelope: one content-free row per ordinary chat invocation at terminalization.</summary>
    ChatRunEnvelope = 1,

    /// <summary>
    ///     Tool-approval decision audit: one content-free row per resolved approval decision (approve / deny /
    ///     timeout). Reuses existing metadata columns — no message content, never encrypted. Excluded from the
    ///     diagnostics view (kind 0) and the run-envelope ledger (kind 1) since each read path filters to its own kind;
    ///     pruned by the same whole-table retention sweep.
    /// </summary>
    ApprovalDecision = 2,

    /// <summary>
    ///     External-integration invocation audit: one content-free row per integration execution at terminalization.
    ///     Reuses existing columns rather than adding a table — trigger name into <c>ModelName</c>, the requesting key
    ///     prefix into <c>Provider</c>, the target agent definition id into <c>ConfigHash</c> — with
    ///     <c>InvocationId</c>, <c>RequestId</c>, <c>TerminalStatus</c>, <c>TraceId</c> and <c>LatencyMs</c> in their
    ///     own columns. Every value is a trigger name, a key prefix, an id or a status: no input, no output and no
    ///     message reaches it. Like kind 2 it binds <c>AgentDefinitionId</c> to <c>Guid.Empty</c> so it shares one
    ///     retention bucket and appears in no per-agent view.
    /// </summary>
    IntegrationInvocation = 3
}

/// <summary>
///     Fields supplied when appending a tool-approval DECISION audit row. Metadata only — supply NO message
///     content and NO tool arguments. <see cref="Category" /> is a <c>ToolCategory</c> enum name, <see cref="Decision" />
///     one of <see cref="ApprovalDecisions" />, and <see cref="Source" /> one of <see cref="ApprovalDecisionSources" /> —
///     all non-sensitive category labels. <see cref="LatencyMs" /> is the request→decision wall-clock in milliseconds.
/// </summary>
public sealed record ApprovalDecisionAuditInput(
    Guid? InvocationId,
    string ToolName,
    string Category,
    string Decision,
    string Source,
    long LatencyMs);

/// <summary>
///     Fields supplied when appending an integration-invocation audit row. Metadata only — every value is a trigger
///     name, a credential prefix, an id or a terminal status. <see cref="TerminalStatus" /> uses the existing
///     envelope vocabulary (<c>completed</c> / <c>failed</c> / <c>cancelled</c>); <see cref="KeyPrefix" /> is audit
///     metadata naming which of a principal's credentials sent the request, and answers no ownership question.
///     <see cref="LatencyMs" /> is the accept-to-terminal wall clock in milliseconds.
/// </summary>
public sealed record IntegrationInvocationAuditInput(
    Guid InvocationId,
    Guid RequestId,
    string TriggerName,
    string KeyPrefix,
    Guid TargetAgentDefinitionId,
    string TerminalStatus,
    string? TraceId,
    long LatencyMs);

/// <summary>
///     Versioned read projection of a durable run-envelope row (always
///     <see cref="AgentExecutionLogRecordKind.ChatRunEnvelope" />). Metadata only — no message content;
///     <see cref="FailureCategory" /> is a category enum name only. <see cref="SchemaVersion" /> lets a reader tell
///     envelope shapes apart as the field set evolves.
/// </summary>
public sealed record AgentRunEnvelopeRecord(
    Guid Id,
    int SchemaVersion,
    Guid AgentDefinitionId,
    Guid? ConversationId,
    Guid? MessageId,
    Guid? InvocationId,
    Guid? RequestId,
    string ModelName,
    string Provider,
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
    long CreatedAtUtc,
    // TRAILING rather than beside TotalTokens, unlike AgentRunEnvelopeResponse which does group them with the other
    // token fields: this is a POSITIONAL record, so a member with a default can only be added at the end — inserting
    // mid-list would either be a breaking positional change for every construction site or not compile at all.
    // Tool-schema token estimate for the turn. DELIBERATELY wider than the int? token members above: the cumulative
    // counter is a long at its source and P-C1 sums this column across a whole session, so narrowing it here would
    // truncate silently. The per-round maximum stays an int, matching its own source.
    long? ToolSchemaTokens = null,
    int? MaxToolSchemaTokens = null);

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

/// <summary>
///     One aggregation bucket of run-envelope token usage for a single (model, <see cref="AgentUsageProviders">provider</see>,
///     UTC day) triple. Token sums are <c>long</c> because a busy day can exceed <see cref="int" />; a run reporting no
///     usage for a field contributes 0. Metadata only — no message content.
/// </summary>
/// <param name="ModelName">Model the runs executed on (part of the group key; may be empty for an envelope written without one).</param>
/// <param name="Provider">Fine-grained runtime provider that served the runs (part of the group key; see <see cref="AgentUsageProviders" />).</param>
/// <param name="DayStartUtcMs">Unix-ms timestamp of UTC midnight opening the day bucket (the group key, day-truncated).</param>
/// <param name="RunCount">Number of run-envelope rows in the bucket.</param>
/// <param name="PromptTokens">Summed prompt/input tokens (missing values counted as 0).</param>
/// <param name="CompletionTokens">Summed completion/output tokens (missing values counted as 0).</param>
/// <param name="ReasoningTokens">Summed reasoning tokens (missing values counted as 0).</param>
/// <param name="TotalTokens">Summed total tokens reported by the model (missing values counted as 0).</param>
public sealed record TokenUsageAggregateRecord(
    string ModelName,
    string Provider,
    long DayStartUtcMs,
    int RunCount,
    long PromptTokens,
    long CompletionTokens,
    long ReasoningTokens,
    long TotalTokens);
