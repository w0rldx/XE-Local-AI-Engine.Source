namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class ScheduledJobRun
{
    public Guid Id { get; set; }

    /// <summary>
    ///     The definition this run was fired from; indexed. Intentionally has NO enforced FK because a run outlives its
    ///     definition (a soft-deleted/removed definition must not cascade away its run history) — same precedent as
    ///     conversation->definition. Plaintext (structural).
    /// </summary>
    public Guid ScheduledJobId { get; set; }

    /// <summary>Template (handler) id captured at fire time, denormalized so history survives definition changes. Plaintext (structural).</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Quartz fire-instance id; unique when present (the upsert idempotency key). Plaintext (structural).</summary>
    public string? QuartzFireInstanceId { get; set; }

    /// <summary>What caused this run to fire. Plaintext (structural).</summary>
    public ScheduledRunTrigger TriggeredBy { get; set; }

    /// <summary>Lifecycle status of the run. Plaintext (structural).</summary>
    public ScheduledRunStatus Status { get; set; }

    /// <summary>Unix-ms instant the trigger was scheduled to fire. Plaintext (structural).</summary>
    public long? ScheduledFireTimeUtc { get; set; }

    /// <summary>Unix-ms instant the run actually started; indexed with <see cref="ScheduledJobId" />. Plaintext (structural).</summary>
    public long? ActualFireTimeUtc { get; set; }

    /// <summary>Unix-ms instant the run reached a terminal status. Plaintext (structural).</summary>
    public long? CompletedAtUtc { get; set; }

    /// <summary>Run duration in milliseconds. Plaintext (structural).</summary>
    public long? DurationMs { get; set; }

    /// <summary>Sanitized one-line run summary. Plaintext (structural).</summary>
    public string? Summary { get; set; }

    /// <summary>
    ///     Optional structured run detail as UTF-8 bytes (JSON). Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>details_json</c>.
    /// </summary>
    public byte[]? DetailsJson { get; set; }

    /// <summary>Sanitized error message — no secrets or stack traces. Plaintext (structural).</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Sanitized internal error detail. Plaintext (structural).</summary>
    public string? ErrorDetails { get; set; }

    /// <summary>Unix-ms instant cancellation was requested for this run. Plaintext (structural).</summary>
    public long? CancellationRequestedAtUtc { get; set; }

    public long CreatedAtUtc { get; set; }
}
