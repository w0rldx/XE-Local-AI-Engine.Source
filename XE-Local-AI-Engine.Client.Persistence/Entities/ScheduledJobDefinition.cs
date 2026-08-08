namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class ScheduledJobDefinition
{
    public Guid Id { get; set; }

    /// <summary>Identifies the job template (handler) this definition runs. Plaintext (structural).</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Operator label for the definition. Plaintext (structural).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional operator notes. Plaintext (structural).</summary>
    public string? Description { get; set; }

    /// <summary>Whether the definition is active and eligible to fire. Plaintext (structural).</summary>
    public bool Enabled { get; set; }

    /// <summary>Trigger shape: cron, one-shot, or simple interval. Plaintext (structural).</summary>
    public ScheduleKind ScheduleKind { get; set; }

    /// <summary>Cron expression when <see cref="ScheduleKind" /> is <see cref="Entities.ScheduleKind.Cron" />. Plaintext (structural).</summary>
    public string? CronExpression { get; set; }

    /// <summary>Repeat interval in seconds for a simple-interval trigger. Plaintext (structural).</summary>
    public long? IntervalSeconds { get; set; }

    /// <summary>Number of times a simple-interval trigger repeats; <c>null</c> means repeat forever. Plaintext (structural).</summary>
    public int? RepeatCount { get; set; }

    /// <summary>Unix-ms instant the trigger becomes active. Plaintext (structural).</summary>
    public long? StartAtUtc { get; set; }

    /// <summary>Unix-ms instant the trigger stops firing. Plaintext (structural).</summary>
    public long? EndAtUtc { get; set; }

    /// <summary>IANA time-zone id the cron expression is evaluated in. Plaintext (structural).</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>What to do when the trigger misses its fire time. Plaintext (structural).</summary>
    public SchedulerMisfirePolicy MisfirePolicy { get; set; }

    /// <summary>Whether overlapping runs of this definition are prevented. Plaintext (structural).</summary>
    public bool PreventOverlap { get; set; }

    /// <summary>Per-run runtime ceiling in seconds before the run is timed out. Plaintext (structural).</summary>
    public int? MaxRuntimeSeconds { get; set; }

    /// <summary>
    ///     Optional opaque job parameters as UTF-8 bytes (JSON). Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>parameter_json</c>.
    /// </summary>
    public byte[]? ParameterJson { get; set; }

    /// <summary>Who created the definition. Plaintext (structural).</summary>
    public ScheduledJobCreator CreatedBy { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }

    /// <summary>Unix-ms instant the definition was most recently disabled; <c>null</c> while enabled. Plaintext (structural).</summary>
    public long? DisabledAtUtc { get; set; }

    /// <summary>Unix-ms instant the definition was soft-deleted; <c>null</c> while live. Plaintext (structural).</summary>
    public long? DeletedAtUtc { get; set; }
}
