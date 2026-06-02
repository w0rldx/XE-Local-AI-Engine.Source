namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A single model-fit utility run snapshot (recommendation or benchmark). The raw utility output, captured stderr
///     and detailed diagnostics are sensitive by default (provider metadata, host topology, local paths, unexpected
///     stderr) and are stored as encrypted UTF-8 byte columns — encrypted at rest by
///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
///     <see cref="NodeEncryptionMaterializationInterceptor" /> with per-column AAD. List/latest projections omit them;
///     only an explicit operator-diagnostics read returns them. A run intentionally has NO enforced FK to its scheduler
///     run (<see cref="CreatedByRunId" />): runs outlive definitions — same no-FK precedent as scheduled_job_runs.
/// </summary>
internal sealed record class ModelFitSnapshot
{
    public Guid Id { get; set; }

    /// <summary>The approved image this run used. Plaintext (structural).</summary>
    public string ApprovedImageId { get; set; } = string.Empty;

    /// <summary>Whether this snapshot is a recommendation or benchmark run. Plaintext (structural).</summary>
    public ModelFitOperation Operation { get; set; }

    /// <summary>Requested use-case (recommend); null for benchmark. Part of the latest-successful key. Plaintext.</summary>
    public string? UseCase { get; set; }

    /// <summary>Provider this run targeted (e.g. <c>ollama</c>). Part of the latest-successful key. Plaintext.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Model name (benchmark), or null for a recommendation run. Part of the latest-successful key. Plaintext.</summary>
    public string? ModelName { get; set; }

    /// <summary>Lifecycle status of the run. Plaintext (structural).</summary>
    public ModelFitRunStatus Status { get; set; }

    /// <summary>Unix-ms instant the run started, or null. Plaintext (structural).</summary>
    public long? StartedAtUtc { get; set; }

    /// <summary>Unix-ms instant the run reached a terminal status, or null. Plaintext (structural).</summary>
    public long? CompletedAtUtc { get; set; }

    /// <summary>Run duration in milliseconds, or null. Plaintext (structural).</summary>
    public long? DurationMs { get; set; }

    /// <summary>Process exit code, or null. Plaintext (structural).</summary>
    public int? ExitCode { get; set; }

    /// <summary>
    ///     Raw utility JSON output as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest using AAD column
    ///     name <c>raw_json</c>. Optional.
    /// </summary>
    public byte[]? RawJson { get; set; }

    /// <summary>
    ///     Sanitized stderr excerpt as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest using AAD column
    ///     name <c>stderr_excerpt</c>. Optional.
    /// </summary>
    public byte[]? StderrExcerpt { get; set; }

    /// <summary>
    ///     Detailed run diagnostics as UTF-8 bytes (JSON; system topology etc). Plaintext while tracked in memory;
    ///     encrypted at rest using AAD column name <c>diagnostics_json</c>. Optional.
    /// </summary>
    public byte[]? DiagnosticsJson { get; set; }

    /// <summary>True for exactly one row per latest-successful key. Plaintext (structural).</summary>
    public bool IsLatestSuccessful { get; set; }

    /// <summary>Scheduler run id when triggered by Quartz, or null. No enforced FK. Plaintext (structural).</summary>
    public Guid? CreatedByRunId { get; set; }

    /// <summary>Unix-ms instant the row was created. Plaintext (structural).</summary>
    public long CreatedAtUtc { get; set; }
}
