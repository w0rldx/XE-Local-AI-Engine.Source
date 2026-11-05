namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Reads a GGUF file header via an HTTP range request (no full download) and extracts the standardized metadata
///     fields populated onto <c>GgufRepoFile</c> (architecture, block/head counts, embedding/context lengths, etc.).
/// </summary>
/// <remarks>
///     Internal — tests feed canned header bytes through a stubbed handler. GGUF v3 layout (verified against the ggml GGUF spec):
///     little-endian <c>magic</c> (<c>0x47 0x47 0x55 0x46</c>), <c>uint32 version</c>, <c>uint64 tensor_count</c>,
///     <c>uint64 metadata_kv_count</c>, then KV pairs — key is <c>uint64 len</c> + UTF-8 bytes, value is <c>uint32 value_type</c> + the
///     typed value. Strings are <c>uint64 len</c> + UTF-8. If the KV block extends past the initially-requested range the reader
///     re-requests a doubled range up to a cap, then surfaces partial (null) fields rather than throwing.
/// </remarks>
internal sealed class GgufHeaderReader
{
    internal const uint GgufMagic = 0x4655_4747; // "GGUF" little-endian (0x47 0x47 0x55 0x46).

    // One advisor run reads more than 256 file headers across the catalog, and early and complete reads are separate keys.
    private const int HeaderCacheMaxEntries = 1024;

    private readonly HttpClient _httpClient;
    private readonly IHfTokenStore _tokenStore;
    private readonly ILogger<GgufHeaderReader> _logger;
    private readonly HuggingFaceOptions _options;
    private readonly TtlCache<GgufHeaderMetadata> _headerCache;

    public GgufHeaderReader(HttpClient httpClient, IHfTokenStore tokenStore, HuggingFaceOptions options, ILogger<GgufHeaderReader> logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _options = options;
        _logger = logger;
        _headerCache = new TtlCache<GgufHeaderMetadata>(timeProvider, HeaderCacheMaxEntries);
    }

    /// <summary>
    ///     Range-reads the GGUF header from <paramref name="repoId" />/<paramref name="fileName" /> at
    ///     <paramref name="revision" /> and extracts the standardized metadata.
    /// </summary>
    /// <remarks>
    ///     Stops at the tokenizer once the architecture block is set (<see cref="GgufHeaderMetadata.IsPartial" />); keys
    ///     after it are not read. Never throws on a short read or a non-GGUF file (all-null metadata instead). Cached for
    ///     <see cref="HuggingFaceOptions.HeaderCacheTtl" /> by repo + filename + resolved revision (immutable), except a
    ///     read whose first range got an HTTP error status: that one returns all-null metadata uncached.
    /// </remarks>
    public Task<GgufHeaderMetadata> ReadHeaderAsync(string repoId, string fileName, string revision, CancellationToken ct)
    {
        return ReadCachedAsync(repoId, fileName, revision, "early", HasArchitectureBlock, ct);
    }

    /// <summary>
    ///     Range-reads the WHOLE key-value section of one file (growing to the hard cap), so keys written after the
    ///     tokenizer are read too. Same tolerance as <see cref="ReadHeaderAsync" />, cached separately.
    /// </summary>
    public Task<GgufHeaderMetadata> ReadCompleteHeaderAsync(string repoId, string fileName, string revision, CancellationToken ct)
    {
        return ReadCachedAsync(repoId, fileName, revision, "complete", static _ => false, ct);
    }

    private async Task<GgufHeaderMetadata> ReadCachedAsync(string repoId,
        string fileName,
        string revision,
        string mode,
        Func<ParsedHeader, bool> isSufficient,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!HuggingFaceRepoId.IsValid(repoId))
        {
            // Header metadata is best-effort: a malformed id reads as "nothing known" and never reaches a token-carrying URL.
            return GgufHeaderMetadata.Empty;
        }

        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision;
        var cacheKey = $"{repoId}::{fileName}::{rev}::{mode}";

        try
        {
            return await _headerCache.GetOrAddAsync(cacheKey, _options.HeaderCacheTtl, token => FetchHeaderAsync(repoId, fileName, rev, isSufficient, token), ct)
                                     .ConfigureAwait(false);
        }
        catch (FirstRangeFailedException)
        {
            // A throwing factory caches nothing, so the next read asks again (a token added later, a lifted rate limit).
            return GgufHeaderMetadata.Empty;
        }
    }

    private async Task<GgufHeaderMetadata> FetchHeaderAsync(string repoId, string fileName, string rev, Func<ParsedHeader, bool> isSufficient, CancellationToken ct)
    {
        var encodedFile = string.Join(separator: '/', fileName.Split('/').Select(Uri.EscapeDataString));
        var url = $"{_options.DownloadBaseUrl.TrimEnd('/')}/{HuggingFaceRepoId.EscapePath(repoId)}/resolve/{Uri.EscapeDataString(rev)}/{encodedFile}";

        var probe = _options.HeaderProbeBytes > 0 ? _options.HeaderProbeBytes : 4L * 1024 * 1024;
        const long hardCap = 64L * 1024 * 1024; // never range-request more than this to read a header.

        try
        {
            var authToken = await _tokenStore.GetTokenAsync(ct).ConfigureAwait(false);
            var firstRange = true;
            return await ReadGrowingAsync(async (requested, token) =>
                    {
                        var bytes = await FetchRangeAsync(url, authToken, requested, token).ConfigureAwait(false);
                        if (bytes is null && firstRange)
                        {
                            throw new FirstRangeFailedException();
                        }

                        firstRange = false;
                        return bytes;
                    },
                    probe,
                    hardCap,
                    isSufficient,
                    ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "GGUF header range read failed for a repo file; returning empty header metadata.");
            return GgufHeaderMetadata.Empty;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("GGUF header range read timed out; returning empty header metadata.");
            return GgufHeaderMetadata.Empty;
        }
    }

    /// <summary>
    ///     Reads the GGUF header from a local <paramref name="filePath" /> and extracts the standardized metadata, using
    ///     the same grow-on-truncation probe loop as the remote path.
    /// </summary>
    /// <remarks>
    ///     <c>context_length</c> sits early in the header (before the large tokenizer arrays) for llama.cpp-written
    ///     GGUFs, so the first modest read virtually always suffices. Fully tolerant: a missing file / non-GGUF content
    ///     / short read / IO error / parse failure returns an all-null <see cref="GgufHeaderMetadata" /> and never
    ///     throws (cancellation excepted).
    /// </remarks>
    public async Task<GgufHeaderMetadata> ReadHeaderFromFileAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        const long initialProbe = 1L * 1024 * 1024; // 1 MiB — context_length lives near the start of the header.
        // tokenizer.chat_template typically sits AFTER the large tokenizer.ggml.tokens vocab array, so it can land several MiB into the header. The grow loop doubles up to this ceiling to capture it
        // for capability detection; it is a local file read, so the larger ceiling costs only a one-time read of an already-installed file.
        const long hardCap = 64L * 1024 * 1024; // doubling ceiling for the local read.

        try
        {
            await using var stream = new FileStream(filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await ReadGrowingAsync((requested, token) => ReadPrefixAsync(stream, requested, token), initialProbe, hardCap, static _ => false, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogDebug(ex, "GGUF local header read failed for a model file; returning empty header metadata.");
            return GgufHeaderMetadata.Empty;
        }
    }

    /// <summary>
    ///     Runs the shared grow-on-truncation probe loop over a byte source that returns the first <c>count</c> bytes of
    ///     the file.
    /// </summary>
    /// <remarks>
    ///     Doubles the probe up to <paramref name="hardCap" /> while parsing reports it ran out of bytes mid-block,
    ///     stopping early when the source returns fewer bytes than requested (the whole short file was read) or when
    ///     <paramref name="isSufficient" /> accepts the truncated parse, which is then flagged
    ///     <see cref="GgufHeaderMetadata.IsPartial" />.
    /// </remarks>
    private static async Task<GgufHeaderMetadata> ReadGrowingAsync(Func<long, CancellationToken, Task<byte[]?>> fetch,
        long initialProbe,
        long hardCap,
        Func<ParsedHeader, bool> isSufficient,
        CancellationToken ct)
    {
        var requested = initialProbe;
        while (true)
        {
            var bytes = await fetch(requested, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                return GgufHeaderMetadata.Empty;
            }

            var parsed = TryParse(bytes);
            if (!parsed.Truncated || requested >= hardCap || bytes.Length < requested)
            {
                // Parsed fully, hit the cap, or the source returned the whole (short) file — accept what we have.
                return parsed.Metadata;
            }

            if (isSufficient(parsed))
            {
                return parsed.Metadata with
                {
                    IsPartial = true
                };
            }

            requested = Math.Min(requested * 2, hardCap);
        }
    }

    /// <summary>
    ///     Remote sufficiency check: the parse was cut at or after the first <c>tokenizer.*</c> key with the fit-relevant
    ///     architecture fields set, so the vocabulary arrays need not be fetched.
    /// </summary>
    /// <remarks>
    ///     GGUF does not guarantee that order: a tool may append a key after the tokenizer. The result is therefore
    ///     flagged partial, and the inspector completes it with one <see cref="ReadCompleteHeaderAsync" /> per architecture.
    /// </remarks>
    private static bool HasArchitectureBlock(ParsedHeader parsed)
    {
        var metadata = parsed.Metadata;
        return parsed.ReachedTokenizer
               && metadata.Architecture is not null
               && metadata.BlockCount is > 0
               && metadata.EmbeddingLength is > 0
               && metadata.AttentionHeadCount is > 0
               && metadata.AttentionHeadCountKV is > 0;
    }

    // Reads up to count bytes from the start of the open stream. Returns null when the file is empty.
    private static async Task<byte[]?> ReadPrefixAsync(FileStream stream, long count, CancellationToken ct)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return null;
        }

        var toRead = (int)Math.Min(count, length);
        var buffer = new byte[toRead];
        stream.Seek(offset: 0, SeekOrigin.Begin);

        var read = 0;
        while (read < toRead)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, toRead - read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == toRead ? buffer : buffer[..read];
    }

    private async Task<byte[]?> FetchRangeAsync(string url, string? authToken, long count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from: 0, Math.Max(val1: 0, count - 1));
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            // Token-bearing requests only; the value is never logged or surfaced.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        }

        using var response = await _httpClient
                                   .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GGUF header range request returned {StatusCode}.", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Parses the GGUF header out of <paramref name="bytes" />. Returns the extracted metadata and whether parsing
    ///     ran out of bytes mid-block (caller may re-request a larger range). Tolerant: bad magic / odd value-types yield
    ///     empty-or-partial metadata, never an exception.
    /// </summary>
    private static ParsedHeader TryParse(byte[] bytes)
    {
        var reader = new GgufSpanReader(bytes);

        if (!reader.TryReadUInt32(out var magic) || magic != GgufMagic)
        {
            return new ParsedHeader(GgufHeaderMetadata.Empty, Truncated: false);
        }

        if (!reader.TryReadUInt32(out _) || // version
            !reader.TryReadUInt64(out _) || // tensor_count
            !reader.TryReadUInt64(out var kvCount))
        {
            return new ParsedHeader(GgufHeaderMetadata.Empty, Truncated: true);
        }

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var reachedTokenizer = false;
        for (ulong i = 0; i < kvCount; i++)
        {
            if (!reader.TryReadString(out var key))
            {
                return new ParsedHeader(Build(values), Truncated: true, reachedTokenizer);
            }

            reachedTokenizer |= key.StartsWith("tokenizer.", StringComparison.Ordinal);

            if (!reader.TryReadUInt32(out var valueType))
            {
                return new ParsedHeader(Build(values), Truncated: true, reachedTokenizer);
            }

            if (!reader.TryReadValue(valueType, out var value))
            {
                return new ParsedHeader(Build(values), Truncated: true, reachedTokenizer);
            }

            if (value is not null)
            {
                values[key] = value;
            }
        }

        return new ParsedHeader(Build(values), Truncated: false, reachedTokenizer);
    }

    /// <summary>
    ///     A parsed GGUF header, whether parsing ran out of bytes mid-block (the caller may re-request more), and whether
    ///     a <c>tokenizer.*</c> key was reached.
    /// </summary>
    private sealed record ParsedHeader(GgufHeaderMetadata Metadata, bool Truncated, bool ReachedTokenizer = false);

    /// <summary>The first range of a remote read got an HTTP error status, so the read has no bytes and must not be cached.</summary>
    private sealed class FirstRangeFailedException : InvalidOperationException
    {
    }

    /// <summary>Projects parsed key-value pairs onto the standardized metadata; shared with <see cref="GgufTensorBlockReader" />.</summary>
    internal static GgufHeaderMetadata Build(IReadOnlyDictionary<string, object> values)
    {
        var architecture = GetString(values, "general.architecture");

        // The quant label rides general.file_type (a uint enum in the GGUF spec), stringified.
        var quantType = GetLongOrNull(values, "general.file_type") is { } ft
            ? ft.ToString(CultureInfo.InvariantCulture)
            : null;

        var paramCount = GetLongOrNull(values, "general.parameter_count");

        string? Arch(string suffix)
        {
            return architecture is null ? null : $"{architecture}.{suffix}";
        }

        var blockCount = GetLongOrNull(values, Arch("block_count"));
        var headCount = GetLongOrNull(values, Arch("attention.head_count"));
        var headCountKv = GetLongOrNull(values, Arch("attention.head_count_kv"));
        var embeddingLength = GetLongOrNull(values, Arch("embedding_length"));
        var contextLength = GetLongOrNull(values, Arch("context_length"));

        // Mixture-of-Experts marker: a dense model omits this key (or writes 0); an MoE model writes its total expert count (e.g. <arch>.expert_count = 8 for Mixtral/Qwen-MoE). Drives the inference
        // profile's is_moe/expert_count so the optimizer measures MoE throughput empirically rather than predicting it.
        var expertCount = GetLongOrNull(values, Arch("expert_count"));

        // Active experts routed per token (e.g. <arch>.expert_used_count = 2 of 8 for a top-2 MoE gate). Combined with
        // expert_count this drives the memory-fit estimator's expert-offload split; null for dense models.
        var expertUsedCount = GetLongOrNull(values, Arch("expert_used_count"));

        // Explicit per-head key/value dimensions (<arch>.attention.key_length / value_length). Preferred by the memory-fit estimator over the derived head_dim = embedding_length / n_heads — the
        // derivation is wrong for families like Qwen3 that pin head_dim (128) independently of the embedding width. Null when the header omits them.
        var attentionKeyLength = GetLongOrNull(values, Arch("attention.key_length"));
        var attentionValueLength = GetLongOrNull(values, Arch("attention.value_length"));

        // Multi-head Latent Attention (deepseek2 family): when BOTH {arch}.attention.key_length_mla and .value_length_mla are present and positive, llama.cpp's is_mla() holds and the cache is
        // allocated as a single latent K tensor with NO V tensor at all. Detection is by these two keys only — never by architecture name.
        var attentionKeyLengthMla = GetLongOrNull(values, Arch("attention.key_length_mla"));
        var attentionValueLengthMla = GetLongOrNull(values, Arch("attention.value_length_mla"));

        // Interleaved sliding-window attention (Gemma family): a positive window means the window-limited layers hold at most this many KV positions instead of the full context. The layer stride
        // (every Nth layer is full attention) comes from an explicit header key when present, else the per-architecture default (Gemma3=6, Gemma2=2).
        var slidingWindow = GetLongOrNull(values, Arch("attention.sliding_window"));
        var slidingWindowPattern = GetLongOrNull(values, Arch("attention.sliding_window_pattern"))
                                   ?? GgufAttentionDefaults.SlidingWindowPattern(architecture);

        // Hybrid recurrent/attention stacks (qwen3next, qwen35, qwen35moe): only every Nth layer holds a KV cache, the rest keep a fixed-size recurrent state. Deliberately NO per-architecture
        // default (llama.cpp assumes 4 when the key is absent): without the key the estimator keeps charging every layer, the conservative side.
        var fullAttentionInterval = GetLongOrNull(values, Arch("full_attention_interval"));
        var decisionType = architecture is null ? null : GetString(values, $"{architecture}.decision.type");

        // The Jinja chat template (when present) reveals the model's real tool / reasoning surface for capability
        // detection. Architecture-independent key; null when the GGUF was written without one (a raw base model).
        var chatTemplate = GetString(values, "tokenizer.chat_template");

        return new GgufHeaderMetadata
        {
            Architecture = architecture,
            QuantType = quantType,
            ParamCount = paramCount,
            SizeLabel = GetString(values, "general.size_label"),
            BlockCount = blockCount,
            AttentionHeadCount = headCount,
            AttentionHeadCountKV = headCountKv,
            EmbeddingLength = embeddingLength,
            ContextLength = contextLength,
            ChatTemplate = chatTemplate,
            ExpertCount = expertCount,
            ExpertUsedCount = expertUsedCount,
            AttentionKeyLength = attentionKeyLength,
            AttentionValueLength = attentionValueLength,
            SlidingWindow = slidingWindow,
            SlidingWindowPattern = slidingWindowPattern,
            AttentionKeyLengthMla = attentionKeyLengthMla,
            AttentionValueLengthMla = attentionValueLengthMla,
            FullAttentionInterval = fullAttentionInterval,

            // llama-server switches a model to the /v1/systemone decision lane on this key; a blank value counts as absent.
            DecisionType = string.IsNullOrWhiteSpace(decisionType) ? null : decisionType.Trim()
        };
    }

    private static string? GetString(IReadOnlyDictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var v) && v is string s ? s : null;
    }

    internal static long? GetLongOrNull(IReadOnlyDictionary<string, object> values, string? key)
    {
        if (key is null || !values.TryGetValue(key, out var v))
        {
            return null;
        }

        return v switch
        {
            long l => l,
            ulong u => u <= long.MaxValue ? (long)u : long.MaxValue,
            double d => (long)d,
            _ => null
        };
    }
}
