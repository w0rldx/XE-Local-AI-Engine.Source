namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Append-only metadata log of a single agent run (adaptive memory diagnostics). Holds NO message content — only
///     latency/token/success telemetry plus ids that link back to the already-encrypted chat tables. The whole row is
///     plaintext (structural) and is NEVER encrypted; <see cref="ErrorClass" /> is an exception type name only, never the
///     exception message or any transcript text.
/// </summary>
internal sealed record class AgentExecutionLog
{
    public Guid Id { get; set; }

    /// <summary>Agent definition the run executed under. Indexed (with <see cref="CreatedAtUtc" />). Plaintext (structural).</summary>
    public Guid AgentDefinitionId { get; set; }

    /// <summary>Conversation the run belonged to, or <c>null</c> when not run inside a conversation. Plaintext (structural).</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>Assistant message the run produced, or <c>null</c>. Links to the encrypted chat message by id. Plaintext (structural).</summary>
    public Guid? MessageId { get; set; }

    /// <summary>Model the run executed on. Plaintext (structural).</summary>
    public string ModelName { get; set; } = string.Empty;

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
}
