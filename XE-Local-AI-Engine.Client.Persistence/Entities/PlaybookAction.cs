namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class PlaybookAction
{
    public Guid Id { get; set; }

    /// <summary>Owning agent definition. Real FK to <c>agent_definitions.id</c> with cascade delete; indexed. Plaintext (structural).</summary>
    public Guid AgentDefinitionId { get; set; }

    /// <summary>Backing int for <see cref="PlaybookActionState" />. Plaintext (structural).</summary>
    public int State { get; set; }

    /// <summary>Backing int for <see cref="PlaybookActionSource" /> (provenance). Plaintext (structural).</summary>
    public int Source { get; set; }

    /// <summary>
    ///     Optional retrieval/trigger hint as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>trigger_condition</c>.
    ///     Advisory/display in P1 (not injected); drives retrieval in later phases.
    /// </summary>
    public byte[]? TriggerCondition { get; set; }

    /// <summary>
    ///     Instruction text injected into the agent's system prompt, as UTF-8 bytes. Plaintext while tracked in memory;
    ///     encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>behavior</c>. Required.
    /// </summary>
    public byte[] Behavior { get; set; } = [];

    /// <summary>Optional topic/tool/intent tag (structural, filterable). Plaintext.</summary>
    public string? Scope { get; set; }

    /// <summary>
    ///     Provenance for an analysis-proposed action: a JSON array of feedback message/conversation ids
    ///     that drove this action. Null for manually-authored actions. Plaintext — these are ids only (no comment
    ///     text), so they are not sensitive and are NOT encrypted.
    /// </summary>
    public string? SourceFeedbackIds { get; set; }

    /// <summary>Analysis-agent confidence in [0,1] for a P3-proposed action; null for manual actions. Plaintext (structural).</summary>
    public double? Confidence { get; set; }

    /// <summary>Regression-gate outcome JSON (ids + pass/fail + counts, no transcripts); null until eval runs; cleared on edit; structural — not sensitive.</summary>
    public string? EvalResult { get; set; }

    /// <summary>Injection order; enabled actions inject ascending by this value. Plaintext (structural).</summary>
    public int Priority { get; set; }

    /// <summary>Per-action audit/dedup counter; bumped on a config-affecting edit (Behavior/Priority/State).</summary>
    public int Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    /// <summary>
    ///     Unix-ms timestamp of the most recent transition into <c>Enabled</c>; the cohort-monitoring clock
    ///     that splits feedback into before/after windows. Null until the action is first enabled; preserved (never
    ///     cleared) on disable so the last-enabled instant survives. Plaintext (a timestamp, structural — not sensitive).
    /// </summary>
    public long? EnabledAtUtc { get; set; }
}
