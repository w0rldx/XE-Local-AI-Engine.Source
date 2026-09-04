namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     Reads a GGUF file header via an HTTP range request (no full download) and extracts the standardized metadata
///     fields populated onto <c>GgufRepoFile</c> (architecture, block/head counts, embedding/context lengths, etc.).
///     Internal — tests feed canned header bytes through a stubbed handler.
/// </summary>
/// <remarks>
///     GGUF v3 layout (verified against the ggml GGUF spec 2026-06-18): little-endian <c>magic</c> (<c>0x47 0x47 0x55 0x46</c>),
///     <c>uint32 version</c>, <c>uint64 tensor_count</c>, <c>uint64 metadata_kv_count</c>, then KV pairs — key is
///     <c>uint64 len</c> + UTF-8 bytes, value is <c>uint32 value_type</c> + the typed value. Strings are <c>uint64 len</c>
///     + UTF-8. If the KV block extends past the initially-requested range the reader re-requests a doubled range up to
///     a cap, then surfaces partial (null) fields rather than throwing.
/// </remarks>
internal sealed class GgufHeaderReader
{
    private const uint GgufMagic = 0x4655_4747; // "GGUF" little-endian (0x47 0x47 0x55 0x46).

    private readonly HttpClient _httpClient;
    private readonly ILogger<GgufHeaderReader> _logger;
    private readonly HuggingFaceOptions _options;
    private readonly TtlCache<GgufHeaderMetadata> _headerCache;

    public GgufHeaderReader(HttpClient httpClient, HuggingFaceOptions options, ILogger<GgufHeaderReader> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _headerCache = new TtlCache<GgufHeaderMetadata>(timeProvider);
    }

    /// <summary>
    ///     Range-reads the GGUF header from <paramref name="repoId" />/<paramref name="fileName" /> at
    ///     <paramref name="revision" /> and extracts the standardized metadata. Never throws on a missing optional key,
    ///     a short read, or a non-GGUF file — returns an all-null <see cref="GgufHeaderMetadata" /> instead. Cached for
    ///     <see cref="HuggingFaceOptions.HeaderCacheTtl" />, keyed by repo + filename + resolved revision — a header is
    ///     immutable for a given resolved revision, so once read it never needs a second range request.
    /// </summary>
    public Task<GgufHeaderMetadata> ReadHeaderAsync(string repoId, string fileName, string revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision;
        var cacheKey = $"{repoId}::{fileName}::{rev}";

        return _headerCache.GetOrAddAsync(cacheKey, _options.HeaderCacheTtl, token => FetchHeaderAsync(repoId, fileName, rev, token), ct);
    }

    private async Task<GgufHeaderMetadata> FetchHeaderAsync(string repoId, string fileName, string rev, CancellationToken ct)
    {
        var url = $"{_options.DownloadBaseUrl.TrimEnd('/')}/{repoId}/resolve/{rev}/{fileName}";

        var probe = _options.HeaderProbeBytes > 0 ? _options.HeaderProbeBytes : 4L * 1024 * 1024;
        const long hardCap = 64L * 1024 * 1024; // never range-request more than this to read a header.

        try
        {
            return await ReadGrowingAsync((requested, token) => FetchRangeAsync(url, requested, token), probe, hardCap, ct)
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
    ///     the same grow-on-truncation probe loop as the remote path. <c>context_length</c> sits early in the header (before
    ///     the large tokenizer arrays) for llama.cpp-written GGUFs, so the first modest read virtually always suffices.
    ///     Fully tolerant: a missing file / non-GGUF content / short read / IO error / parse failure returns an all-null
    ///     <see cref="GgufHeaderMetadata" /> and never throws (cancellation excepted).
    /// </summary>
    public async Task<GgufHeaderMetadata> ReadHeaderFromFileAsync(string filePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        const long initialProbe = 1L * 1024 * 1024; // 1 MiB — context_length lives near the start of the header.
        // tokenizer.chat_template typically sits AFTER the large tokenizer.ggml.tokens vocab array, so it can land
        // several MiB into the header. The grow loop doubles up to this ceiling to capture it for capability detection;
        // it is a local file read, so the larger ceiling costs only a one-time read of an already-installed file.
        const long hardCap = 64L * 1024 * 1024; // doubling ceiling for the local read.

        try
        {
            await using var stream = new FileStream(filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return await ReadGrowingAsync((requested, token) => ReadPrefixAsync(stream, requested, token), initialProbe, hardCap, ct)
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
    ///     the file. Doubles the probe up to <paramref name="hardCap" /> while parsing reports it ran out of bytes
    ///     mid-block, stopping early when the source returns fewer bytes than requested (the whole short file was read).
    /// </summary>
    private static async Task<GgufHeaderMetadata> ReadGrowingAsync(Func<long, CancellationToken, Task<byte[]?>> fetch,
        long initialProbe,
        long hardCap,
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

            var (metadata, truncated) = TryParse(bytes);
            if (!truncated || requested >= hardCap || bytes.Length < requested)
            {
                // Parsed fully, hit the cap, or the source returned the whole (short) file — accept what we have.
                return metadata;
            }

            requested = Math.Min(requested * 2, hardCap);
        }
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

    private async Task<byte[]?> FetchRangeAsync(string url, long count, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(from: 0, Math.Max(val1: 0, count - 1));

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
        var reader = new SpanReader(bytes);

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
        for (ulong i = 0; i < kvCount; i++)
        {
            if (!reader.TryReadGgufString(out var key))
            {
                return new ParsedHeader(Build(values), Truncated: true);
            }

            if (!reader.TryReadUInt32(out var valueType))
            {
                return new ParsedHeader(Build(values), Truncated: true);
            }

            if (!TryReadValue(ref reader, valueType, out var value, out var ranOut))
            {
                return new ParsedHeader(Build(values), ranOut);
            }

            if (value is not null)
            {
                values[key] = value;
            }
        }

        return new ParsedHeader(Build(values), Truncated: false);
    }

    /// <summary>A parsed GGUF header plus whether parsing ran out of bytes mid-block (the caller may re-request more).</summary>
    private sealed record ParsedHeader(GgufHeaderMetadata Metadata, bool Truncated);

    private static GgufHeaderMetadata Build(IReadOnlyDictionary<string, object> values)
    {
        var architecture = GetString(values, "general.architecture");

        // The quant label rides general.file_type (a uint enum in the GGUF spec), stringified.
        var quantType = TryGetLong(values, "general.file_type") is { } ft
            ? ft.ToString(CultureInfo.InvariantCulture)
            : null;

        var paramCount = TryGetLong(values, "general.parameter_count");

        string? Arch(string suffix)
        {
            return architecture is null ? null : $"{architecture}.{suffix}";
        }

        var blockCount = TryGetLong(values, Arch("block_count"));
        var headCount = TryGetLong(values, Arch("attention.head_count"));
        var headCountKv = TryGetLong(values, Arch("attention.head_count_kv"));
        var embeddingLength = TryGetLong(values, Arch("embedding_length"));
        var contextLength = TryGetLong(values, Arch("context_length"));

        // Mixture-of-Experts marker: a dense model omits this key (or writes 0); an MoE model writes its total expert
        // count (e.g. <arch>.expert_count = 8 for Mixtral/Qwen-MoE). Drives the inference profile's is_moe/expert_count
        // so the optimizer measures MoE throughput empirically rather than predicting it.
        var expertCount = TryGetLong(values, Arch("expert_count"));

        // Active experts routed per token (e.g. <arch>.expert_used_count = 2 of 8 for a top-2 MoE gate). Combined with
        // expert_count this drives the memory-fit estimator's expert-offload split; null for dense models.
        var expertUsedCount = TryGetLong(values, Arch("expert_used_count"));

        // Explicit per-head key/value dimensions (<arch>.attention.key_length / value_length). Preferred by the memory-fit
        // estimator over the derived head_dim = embedding_length / n_heads — the derivation is wrong for families like
        // Qwen3 that pin head_dim (128) independently of the embedding width. Null when the header omits them.
        var attentionKeyLength = TryGetLong(values, Arch("attention.key_length"));
        var attentionValueLength = TryGetLong(values, Arch("attention.value_length"));

        // Multi-head Latent Attention (deepseek2 family): when BOTH {arch}.attention.key_length_mla and
        // .value_length_mla are present and positive, llama.cpp's is_mla() holds and the cache is allocated as a single
        // latent K tensor with NO V tensor at all. Detection is by these two keys only — never by architecture name.
        var attentionKeyLengthMla = TryGetLong(values, Arch("attention.key_length_mla"));
        var attentionValueLengthMla = TryGetLong(values, Arch("attention.value_length_mla"));

        // Interleaved sliding-window attention (Gemma family): a positive window means the window-limited layers hold at
        // most this many KV positions instead of the full context. The layer stride (every Nth layer is full attention)
        // comes from an explicit header key when present, else the per-architecture default (Gemma3=6, Gemma2=2).
        var slidingWindow = TryGetLong(values, Arch("attention.sliding_window"));
        var slidingWindowPattern = TryGetLong(values, Arch("attention.sliding_window_pattern"))
                                   ?? GgufAttentionDefaults.SlidingWindowPattern(architecture);

        // The Jinja chat template (when present) reveals the model's real tool / reasoning surface for capability
        // detection. Architecture-independent key; null when the GGUF was written without one (a raw base model).
        var chatTemplate = GetString(values, "tokenizer.chat_template");

        return new GgufHeaderMetadata(architecture,
            quantType,
            paramCount,
            blockCount,
            headCount,
            headCountKv,
            embeddingLength,
            contextLength,
            chatTemplate,
            expertCount,
            expertUsedCount,
            attentionKeyLength,
            attentionValueLength,
            slidingWindow,
            slidingWindowPattern,
            attentionKeyLengthMla,
            attentionValueLengthMla);
    }

    private static string? GetString(IReadOnlyDictionary<string, object> values, string key)
    {
        return values.TryGetValue(key, out var v) && v is string s ? s : null;
    }

    private static long? TryGetLong(IReadOnlyDictionary<string, object> values, string? key)
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

    /// <summary>
    ///     Reads one GGUF value of <paramref name="valueType" />. Arrays and unwanted scalar types are consumed (to keep
    ///     the cursor aligned) but yield a <see langword="null" /> value. <paramref name="ranOut" /> signals the buffer
    ///     ended mid-value.
    /// </summary>
    private static bool TryReadValue(ref SpanReader reader, uint valueType, out object? value, out bool ranOut)
    {
        value = null;
        ranOut = false;

        switch (valueType)
        {
            case 0: // UINT8
            case 1: // INT8
            case 7: // BOOL
                if (!reader.TryReadByte(out var b))
                {
                    ranOut = true;
                    return false;
                }

                value = (long)b;
                return true;
            case 2: // UINT16
                if (!reader.TryReadUInt16(out var u16))
                {
                    ranOut = true;
                    return false;
                }

                value = (long)u16;
                return true;
            case 3: // INT16
                if (!reader.TryReadInt16(out var i16))
                {
                    ranOut = true;
                    return false;
                }

                value = (long)i16;
                return true;
            case 4: // UINT32
                if (!reader.TryReadUInt32(out var u32))
                {
                    ranOut = true;
                    return false;
                }

                value = (long)u32;
                return true;
            case 5: // INT32
                if (!reader.TryReadInt32(out var i32))
                {
                    ranOut = true;
                    return false;
                }

                value = (long)i32;
                return true;
            case 6: // FLOAT32
                if (!reader.TryReadFloat32(out var f32))
                {
                    ranOut = true;
                    return false;
                }

                value = (double)f32;
                return true;
            case 8: // STRING
                if (!reader.TryReadGgufString(out var s))
                {
                    ranOut = true;
                    return false;
                }

                value = s;
                return true;
            case 10: // UINT64
                if (!reader.TryReadUInt64(out var u64))
                {
                    ranOut = true;
                    return false;
                }

                value = u64;
                return true;
            case 11: // INT64
                if (!reader.TryReadInt64(out var i64))
                {
                    ranOut = true;
                    return false;
                }

                value = i64;
                return true;
            case 12: // FLOAT64
                if (!reader.TryReadFloat64(out var f64))
                {
                    ranOut = true;
                    return false;
                }

                value = f64;
                return true;
            case 9: // ARRAY — consume to stay aligned; we don't surface array-valued metadata here.
                return TrySkipArray(ref reader, out ranOut);
            default:
                // Unknown value type — we can't know its width, so we cannot safely continue.
                ranOut = true;
                return false;
        }
    }

    private static bool TrySkipArray(ref SpanReader reader, out bool ranOut)
    {
        ranOut = false;
        if (!reader.TryReadUInt32(out var elementType) || !reader.TryReadUInt64(out var length))
        {
            ranOut = true;
            return false;
        }

        for (ulong i = 0; i < length; i++)
        {
            if (!TryReadValue(ref reader, elementType, out _, out ranOut))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Forward-only little-endian span reader over the fetched header bytes.</summary>
    private ref struct SpanReader(ReadOnlySpan<byte> span)
    {
        private readonly ReadOnlySpan<byte> _span = span;
        private int _position;

        private bool TryTake(int count, out ReadOnlySpan<byte> slice)
        {
            if (count < 0 || _position + count > _span.Length)
            {
                slice = default;
                return false;
            }

            slice = _span.Slice(_position, count);
            _position += count;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            if (TryTake(count: 1, out var s))
            {
                value = s[0];
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt16(out ushort value)
        {
            if (TryTake(count: 2, out var s))
            {
                value = BinaryPrimitives.ReadUInt16LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt16(out short value)
        {
            if (TryTake(count: 2, out var s))
            {
                value = BinaryPrimitives.ReadInt16LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt32(out uint value)
        {
            if (TryTake(count: 4, out var s))
            {
                value = BinaryPrimitives.ReadUInt32LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt32(out int value)
        {
            if (TryTake(count: 4, out var s))
            {
                value = BinaryPrimitives.ReadInt32LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadUInt64(out ulong value)
        {
            if (TryTake(count: 8, out var s))
            {
                value = BinaryPrimitives.ReadUInt64LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadInt64(out long value)
        {
            if (TryTake(count: 8, out var s))
            {
                value = BinaryPrimitives.ReadInt64LittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadFloat32(out float value)
        {
            if (TryTake(count: 4, out var s))
            {
                value = BinaryPrimitives.ReadSingleLittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadFloat64(out double value)
        {
            if (TryTake(count: 8, out var s))
            {
                value = BinaryPrimitives.ReadDoubleLittleEndian(s);
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryReadGgufString(out string value)
        {
            value = string.Empty;
            if (!TryReadUInt64(out var length))
            {
                return false;
            }

            if (length > int.MaxValue || !TryTake((int)length, out var bytes))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
    }
}

/// <summary>
///     Standardized GGUF header metadata extracted via a range read. Any field absent from the header is
///     <see langword="null" />.
/// </summary>
internal sealed record GgufHeaderMetadata(
    string? Architecture,
    string? QuantType,
    long? ParamCount,
    long? BlockCount,
    long? AttentionHeadCount,
    long? AttentionHeadCountKV,
    long? EmbeddingLength,
    long? ContextLength,
    string? ChatTemplate,
    long? ExpertCount,
    long? ExpertUsedCount,
    long? AttentionKeyLength = null,
    long? AttentionValueLength = null,
    long? SlidingWindow = null,
    long? SlidingWindowPattern = null,
    long? AttentionKeyLengthMla = null,
    long? AttentionValueLengthMla = null)
{
    public static GgufHeaderMetadata Empty { get; } = new(Architecture: null, QuantType: null, ParamCount: null, BlockCount: null, AttentionHeadCount: null, AttentionHeadCountKV: null,
        EmbeddingLength: null, ContextLength: null, ChatTemplate: null, ExpertCount: null, ExpertUsedCount: null,
        AttentionKeyLength: null, AttentionValueLength: null, SlidingWindow: null, SlidingWindowPattern: null,
        AttentionKeyLengthMla: null, AttentionValueLengthMla: null);

    /// <summary>True when the GGUF declares a positive expert count — a Mixture-of-Experts model (dense models omit it).</summary>
    public bool IsMoe => ExpertCount is > 0;
}
