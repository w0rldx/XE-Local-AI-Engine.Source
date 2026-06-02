namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A single measured benchmark row projected from a benchmark snapshot. The structural metrics are plaintext; the
///     raw benchmark output and detailed diagnostics are sensitive and stored as encrypted UTF-8 byte columns —
///     encrypted at rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
///     <see cref="NodeEncryptionMaterializationInterceptor" />. The AAD column names are deliberately distinct from the
///     snapshot's (<c>bench_raw_json</c>/<c>bench_diagnostics_json</c>) to avoid cross-entity AAD collision. Cascades
///     when its parent snapshot is deleted.
/// </summary>
internal sealed record class ModelFitBenchmark
{
    public Guid Id { get; set; }

    /// <summary>Parent snapshot; real FK with cascade delete, indexed. Plaintext (structural).</summary>
    public Guid SnapshotId { get; set; }

    /// <summary>Benchmarked model name. Plaintext.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Provider name (e.g. <c>ollama</c>). Plaintext.</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Measured tokens per second, or null. Plaintext.</summary>
    public double? TokensPerSecond { get; set; }

    /// <summary>Time to first token in milliseconds, or null. Plaintext.</summary>
    public double? TtftMs { get; set; }

    /// <summary>Total latency in milliseconds, or null. Plaintext.</summary>
    public double? TotalLatencyMs { get; set; }

    /// <summary>Number of measured runs, or null. Plaintext.</summary>
    public int? Runs { get; set; }

    /// <summary>
    ///     Raw benchmark JSON output as UTF-8 bytes. Plaintext while tracked in memory; encrypted at rest using AAD column
    ///     name <c>bench_raw_json</c>. Optional.
    /// </summary>
    public byte[]? RawJson { get; set; }

    /// <summary>
    ///     Detailed benchmark diagnostics as UTF-8 bytes (JSON). Plaintext while tracked in memory; encrypted at rest using
    ///     AAD column name <c>bench_diagnostics_json</c>. Optional.
    /// </summary>
    public byte[]? DiagnosticsJson { get; set; }
}
