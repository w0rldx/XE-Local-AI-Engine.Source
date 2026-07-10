namespace XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Configuration for the Hugging Face GGUF discovery + store. Bound via the Options pattern under the
///     <see cref="SectionName" /> configuration section.
/// </summary>
public sealed class HuggingFaceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "HuggingFace";

    /// <summary>Base URL for the Hub REST API (model listing + tree).</summary>
    public string HubBaseUrl { get; set; } = "https://huggingface.co";

    /// <summary>Base URL for file downloads (the <c>/{repo}/resolve/{rev}/{file}</c> surface).</summary>
    public string DownloadBaseUrl { get; set; } = "https://huggingface.co";

    /// <summary>Absolute directory under which downloaded GGUF files and the registry manifest live.</summary>
    public string ModelsDirectory { get; set; } = string.Empty;

    /// <summary>Hard disk-guard safety margin in bytes required on top of the file size before a download starts.</summary>
    public long DiskMarginBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Quant selected when a request does not specify one.</summary>
    public string DefaultQuant { get; set; } = "Q4_K_M";

    /// <summary>Maximum bytes range-requested to read a GGUF header during repo inspection (re-requested if larger).</summary>
    public long HeaderProbeBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Maximum transient-failure retry attempts for a download before surfacing a network failure.</summary>
    public int MaxDownloadRetries { get; set; } = 4;

    /// <summary>
    ///     Maximum concurrent GGUF header range reads during a single repo inspection. A repo can ship 10-25 quant
    ///     variants; bounding concurrency keeps inspection fast without opening unlimited simultaneous HTTP requests.
    /// </summary>
    public int HeaderReadConcurrency { get; set; } = 6;

    /// <summary>
    ///     TTL for cached Hugging Face Hub search listings and per-repo blob listings. Both drift slowly (download/like
    ///     counts, occasional new commits), so a multi-hour TTL avoids re-fetching on every advisor refresh. A value
    ///     <c>&lt;= 0</c> disables this cache.
    /// </summary>
    public TimeSpan HubMetadataCacheTtl { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     TTL for cached GGUF header reads, keyed by repo + filename + resolved revision. A header is immutable for a
    ///     given resolved revision, so a long TTL is safe; the default effectively never expires within a session. A
    ///     value <c>&lt;= 0</c> disables this cache.
    /// </summary>
    public TimeSpan HeaderCacheTtl { get; set; } = TimeSpan.FromDays(30);
}
