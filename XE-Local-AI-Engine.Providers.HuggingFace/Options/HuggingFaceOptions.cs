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
    ///     Read-idle timeout (seconds) for the download body-copy loop. The download uses
    ///     <c>HttpCompletionOption.ResponseHeadersRead</c>, so the HttpClient timeout covers only the response HEADERS; a
    ///     CDN that accepts the connection and then stalls mid-body would otherwise hang the copy forever with no deadline
    ///     on <c>Stream.ReadAsync</c>. This bounds the gap between two successful reads: if no bytes arrive within the
    ///     window the read is cancelled and surfaced as a TRANSIENT network failure, so the existing resume/retry path
    ///     (<see cref="MaxDownloadRetries" />, <c>.part</c> resume) re-attempts from where it stalled. A value
    ///     <c>&lt;= 0</c> disables the idle bound.
    /// </summary>
    public int DownloadReadIdleTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum concurrent GGUF header range reads during a single repo inspection. A repo can ship 10-25 quant
    ///     variants; bounding concurrency keeps inspection fast without opening unlimited simultaneous HTTP requests.
    /// </summary>
    public int HeaderReadConcurrency { get; set; } = 6;

    /// <summary>
    ///     Number of parallel HTTP byte-range connections used to fetch ONE large model file. A multi-GB GGUF download
    ///     from Hugging Face's CDN is per-connection throughput limited, so splitting it across a handful of streams is
    ///     what <c>hf_transfer</c>/<c>aria2c</c> do and is where the wall-clock win comes from; 4 is the point past
    ///     which added streams stop paying. Clamped to 1-16 at the point of use (the same convention as
    ///     <see cref="HeaderReadConcurrency" />): <c>1</c> — or any value below it — is exactly the single-stream
    ///     download with no range probe and no resume sidecar, and 16 is the ceiling because Hugging Face throttles
    ///     per-IP well before that, so more sockets buy no throughput and only widen the failure surface. Parallel mode
    ///     ADDITIONALLY requires a known file size of at least <see cref="ParallelDownloadMinimumBytes" /> and an origin
    ///     that honours <c>Range</c>; when either does not hold the download falls back to the single stream by itself.
    /// </summary>
    public int DownloadConnections { get; set; } = 4;

    /// <summary>
    ///     Smallest file size (bytes) worth splitting across <see cref="DownloadConnections" /> connections. Below this
    ///     the extra range probe and the per-connection TLS handshakes cost more than the parallelism returns. 64 MiB
    ///     sits far under any real GGUF weight file and above the tokenizer/config/companion files that share this
    ///     download path, so in practice only the weights are parallelised.
    /// </summary>
    public long ParallelDownloadMinimumBytes { get; set; } = 64L * 1024 * 1024;

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
