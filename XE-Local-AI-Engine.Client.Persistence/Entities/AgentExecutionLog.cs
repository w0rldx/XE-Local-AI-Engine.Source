namespace XE_Local_AI_Engine.Client.Persistence.Entities;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Append-only metadata log of a single agent run. FOUR producers share this table, distinguished by
///     <see cref="RecordKind" />: adaptive-memory diagnostics (kind 0, one row per memory-enabled run), the durable
///     per-invocation run envelope (kind 1, one content-free row per ordinary chat invocation at terminalization),
///     the tool-approval decision audit (kind 2) and the external-integration invocation audit (kind 3).
///     Every read and aggregate must filter by <see cref="RecordKind" />, because column meanings are overloaded
///     across the four.
///     Holds NO message content — only latency/token/status telemetry plus ids that link back to the
///     already-encrypted chat tables. The whole row is plaintext (structural) and is NEVER encrypted;
///     <see cref="ErrorClass" /> is an exception type name (memory rows) or a <c>FailureCategory</c> enum name (envelope
///     rows) only, never the exception message or any transcript text.
/// </summary>
internal sealed record class AgentExecutionLog
{
    public Guid Id { get; set; }

    /// <summary>
    ///     Discriminates the row's producer: 0 = adaptive-memory diagnostics, 1 = chat run envelope,
    ///     2 = tool-approval decision audit, 3 = integration invocation. Mirrors <c>AgentExecutionLogRecordKind</c>.
    ///     Existing rows backfill to 0. Plaintext (structural).
    /// </summary>
    public int RecordKind { get; set; }

    /// <summary>Envelope shape version so the run-envelope fields can evolve without a discriminator change. 0 for memory rows. Plaintext (structural).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>
    ///     Agent definition the run executed under. For memory rows this is the real agent id. For envelope rows it is
    ///     the bound agent id copied from the winning assistant-message write when an agent was bound (so the envelope
    ///     can never disagree with the row), or <see cref="System.Guid.Empty" /> when the run had no bound agent.
    ///     Indexed (with <see cref="CreatedAtUtc" />). Plaintext (structural).
    /// </summary>
    public Guid AgentDefinitionId { get; set; }

    /// <summary>Conversation the run belonged to, or <c>null</c> when not run inside a conversation. Plaintext (structural).</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Assistant message the run produced, or <c>null</c>. Links to the encrypted chat message by id. Plaintext (structural).</summary>
    public Guid? MessageId { get; set; }

    /// <summary>Model the run executed on. Plaintext (structural).</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    ///     Fine-grained runtime provider that served the run (a non-sensitive category label): <c>local</c> (llama.cpp),
    ///     <c>ollama</c>, <c>codex</c>, <c>azure</c>, or <c>unknown</c> (fallback / rows written before the dimension
    ///     existed). Never encrypted — a category label like <see cref="ModelName" />, not content. Existing rows and any
    ///     envelope written without a resolved provider backfill to <c>unknown</c> via the column default. Plaintext (structural).
    /// </summary>
    public string Provider { get; set; } = AgentUsageProviders.Unknown;

    /// <summary>Runtime-package config hash for the run. Plaintext (structural).</summary>
    public string ConfigHash { get; set; } = string.Empty;

    /// <summary>End-to-end run latency in milliseconds. Plaintext (structural).</summary>
    public long LatencyMs { get; set; }

    /// <summary>Prompt/input tokens reported by the model, or <c>null</c> when the model did not report usage. Plaintext (structural).</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion/output tokens reported by the model, or <c>null</c> when the model did not report usage. Plaintext (structural).</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>Whether the run completed successfully. Plaintext (structural).</summary>
    public bool Success { get; set; }

    /// <summary>
    ///     Exception type name only when the run failed (e.g. <c>HttpRequestException</c>), or <c>null</c> on success.
    ///     NEVER the exception message or any transcript text. Plaintext (structural).
    /// </summary>
    public string? ErrorClass { get; set; }

    /// <summary>Unix-ms timestamp when the log row was written. Indexed (with <see cref="AgentDefinitionId" />). Plaintext (structural).</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>Invocation the run envelope belongs to; <c>null</c> for memory rows and interrupted envelopes with no run state. Plaintext (structural).</summary>
    public Guid? InvocationId { get; set; }

    /// <summary>Request that drove the run (correlation id); <c>null</c> for memory rows. Plaintext (structural).</summary>
    public Guid? RequestId { get; set; }

    /// <summary>
    ///     Terminal outcome of an envelope run (<c>completed</c> / <c>failed</c> / <c>cancelled</c> / <c>interrupted</c>).
    ///     Carries the granular outcome that <see cref="Success" /> alone cannot (cancelled vs failed vs interrupted).
    ///     <c>null</c> for memory rows. Plaintext (structural).
    /// </summary>
    public string? TerminalStatus { get; set; }

    /// <summary>W3C trace id of the run's ambient activity when one was present at terminalization, for cross-correlation with exported traces; else <c>null</c>. Plaintext (structural).</summary>
    public string? TraceId { get; set; }

    /// <summary>Number of streamed content chunks observed for the run; <c>null</c> when not known at the seam. Plaintext (structural).</summary>
    public int? ContentChunkCount { get; set; }

    /// <summary>Number of streamed reasoning chunks observed for the run; <c>null</c> when not known at the seam. Plaintext (structural).</summary>
    public int? ReasoningChunkCount { get; set; }

    /// <summary>Reasoning/thinking tokens reported by the model, or <c>null</c> when not reported. Plaintext (structural).</summary>
    public int? ReasoningTokens { get; set; }

    /// <summary>Total tokens reported by the model (prompt + completion + reasoning), or <c>null</c> when not reported. Plaintext (structural).</summary>
    public int? TotalTokens { get; set; }

    /// <summary>Unix-ms timestamp when the run started (turn open), or <c>null</c> when not known at the seam (e.g. an interrupted stream with no run state). Plaintext (structural).</summary>
    public long? StartedAtUtc { get; set; }

    /// <summary>
    ///     Estimated tool-schema tokens the turn spent, CUMULATIVE across its provider rounds, or <c>null</c> when the
    ///     seam reported none (a memory row, or an envelope written by the restart-recovery backfill). A
    ///     <c>long</c> because its source is: the budgeter accumulates it with <c>Interlocked.Add</c> over every round.
    ///     Plaintext (structural) — a count, never a tool name.
    /// </summary>
    public long? ToolSchemaTokens { get; set; }

    /// <summary>
    ///     The largest single round's estimated tool-schema token count for the turn, or <c>null</c> when not reported.
    ///     An <c>int</c>, matching its source (a per-round maximum, not an accumulation). Plaintext (structural).
    /// </summary>
    public int? MaxToolSchemaTokens { get; set; }

    /// <summary>
    ///     The tier a turn authored with reasoning effort <c>auto</c> was dispatched to (<c>fast</c>, <c>normal</c> or
    ///     <c>deep</c>), or <c>null</c> on every other turn and every pre-migration row. A closed-vocabulary category
    ///     label, never free text. Plaintext (structural).
    /// </summary>
    public string? DispatchedTier { get; set; }

    /// <summary>
    ///     The effort the turn was AUTHORED with when a dispatch happened — <c>auto</c> — or <c>null</c> otherwise. It
    ///     is what separates the pre-<c>auto</c> population from the dispatched one in the same measurement, which a
    ///     tier alone cannot do. Plaintext (structural).
    /// </summary>
    public string? AuthoredEffort { get; set; }
}
