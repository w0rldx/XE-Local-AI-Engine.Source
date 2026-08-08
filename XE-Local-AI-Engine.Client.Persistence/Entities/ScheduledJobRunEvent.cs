namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class ScheduledJobRunEvent
{
    public Guid Id { get; set; }

    /// <summary>Owning run. Real FK to <c>scheduled_job_runs.id</c> with cascade delete; indexed with <see cref="Sequence" />. Plaintext (structural).</summary>
    public Guid RunId { get; set; }

    /// <summary>Monotonic per-run ordering of events. Plaintext (structural).</summary>
    public int Sequence { get; set; }

    /// <summary>Severity of the event. Plaintext (structural).</summary>
    public ScheduledRunEventLevel Level { get; set; }

    /// <summary>Sanitized event message. Plaintext (structural).</summary>
    public string? Message { get; set; }

    /// <summary>
    ///     Optional structured event payload as UTF-8 bytes (JSON). Plaintext while tracked in memory; encrypted at rest
    ///     by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>data_json</c>.
    /// </summary>
    public byte[]? DataJson { get; set; }

    public long OccurredAtUtc { get; set; }
}
