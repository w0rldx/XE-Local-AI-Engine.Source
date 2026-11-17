namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>What every job runs. Fixed or bounded here so no request can widen what a job measures or stores past these limits.</summary>
public static class ModelLabTraceSpec
{
    /// <summary>Separate from the trainer's: the two scripts change independently. Pinned by <c>modellablib.CONTRACT_VERSION</c>.</summary>
    public const int ContractVersion = 5;

    public const int MaxNewTokens = 128;

    public const int Seed = 1234;

    /// <summary>A request may trace every block of the deepest checkpoint the lab loads; each layer costs about as much generation time as the rest.</summary>
    public const int MaxTraceLayers = 64;

    /// <summary>Texts are written whole; a trace result that would pass 0.9 MiB has its candidate, then token, texts cut to 64 escaped bytes (<c>modellablib.fitted_trace_result</c>; <c>test_modellablib.py</c> pins it).</summary>
    public const int MaxResultBytes = 1024 * 1024;

    public const int MaxPromptCharacters = 8000;

    public const int MaxContrastPrompts = 32;

    public const int MaxContrastPromptCharacters = 2000;

    /// <summary>On top of the request's own prompt: every arm runs them all, one load each, so they cost little and stay readable.</summary>
    public const int MaxAdditionalTestPrompts = 7;

    /// <summary>The range the vector stays meaningful in for a request. llama.cpp would take more; a sweep is not this feature's job.</summary>
    public const double MinSteeringScale = 0.001;

    public const double MaxSteeringScale = 20;

    /// <summary>The context each arm's server gets: room for a test prompt at its character limit and the output.</summary>
    public const int ArmContextTokens = 8192;

    /// <summary>How deep a default steering layer sits. A measured vector sat at about this depth on the 0.8B.</summary>
    private const double DefaultSteeringDepth = 0.6;

    /// <summary>Keys kept per attention row and head; the rest of the row is stored as one mass.</summary>
    public const int AttentionTopKeys = 32;

    /// <summary>Full-attention blocks a trace records attention at. No user pick: more than this are thinned evenly.</summary>
    public const int MaxAttentionLayers = 8;

    public const int MaxAttentionHeads = 32;

    private const string LinearAttention = "linear_attention";

    private const string FullAttention = "full_attention";

    /// <summary>Measured 6.5 to 8.3 bytes per stored key (position and weight) on the 0.8B; 10 leaves room for longer positions.</summary>
    private const int AttentionBytesPerKey = 10;

    private const int AttentionCapMargin = 2;

    /// <summary>Measured on a trace: one token's record at one layer is about 162 bytes.</summary>
    private const int TraceBytesPerTokenLayer = 162;

    private const int TraceCapMargin = 4;

    /// <summary>The script's room for its footer line, <c>modellablib.FOOTER_RESERVE_BYTES</c>.</summary>
    private const int TraceFooterReserveBytes = 160;

    /// <summary>The cap from when every trace had three layers. No layer count gets less, so no trace that fitted then is refused now.</summary>
    private const int MinTraceByteCap = 256 * 1024;

    /// <summary>Tokens a logit lens keeps per step and block, besides the chosen one.</summary>
    public const int LensTopTokens = 5;

    /// <summary>Worst-case field widths, not a measurement: ids 5 x 7 + 3, logprobs 5 x 11 + 3, chosen 11, rank 7, entropy 7.</summary>
    private const int LensBytesPerBlock = 121;

    /// <summary>One <c>"248319":"..."</c> entry with its text cut to 64 escaped bytes.</summary>
    private const int LensBytesPerText = 76;

    private const int LensBytesPerLine = 64;

    private const string LensLinePrefix = "{\"lens\"";

    private const string LensFooterPrefix = "{\"lensFooter\"";

    /// <summary>The largest file a trace can write: every layer traced and a lens at every block; with <see cref="MaxAttentionByteCap" />, the bound on what the blob store keeps.</summary>
    public static readonly int MaxTraceByteCap = TraceByteCapFor(MaxTraceLayers) + LensByteCapFor(MaxTraceLayers);

    /// <summary>The largest attention cap a checkpoint can ask for: about 20 MiB.</summary>
    public static readonly int MaxAttentionByteCap = AttentionByteCapFor(MaxAttentionLayers, MaxAttentionHeads);

    /// <summary>Twice what every head's top keys cost at these many layers for <see cref="MaxNewTokens" /> rows, plus the footer's reserve.</summary>
    public static int AttentionByteCapFor(int layerCount, int heads)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layerCount, MaxAttentionLayers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heads);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(heads, MaxAttentionHeads);
        return (layerCount * heads * MaxNewTokens * AttentionTopKeys * AttentionBytesPerKey * AttentionCapMargin) + TraceFooterReserveBytes;
    }

    /// <summary>Every full-attention block, or <see cref="MaxAttentionLayers" /> of them evenly spaced, first and last kept. Empty when there is none.</summary>
    public static IReadOnlyList<int> AttentionLayersFor(IReadOnlyList<string> layerTypes)
    {
        ArgumentNullException.ThrowIfNull(layerTypes);
        var full = Enumerable.Range(0, layerTypes.Count).Where(layer => string.Equals(layerTypes[layer], FullAttention, StringComparison.Ordinal)).ToArray();
        return full.Length <= MaxAttentionLayers
            ? full
            : [.. Enumerable.Range(0, MaxAttentionLayers).Select(pick => full[pick * (full.Length - 1) / (MaxAttentionLayers - 1)])];
    }

    /// <summary>Four times what <see cref="MaxNewTokens" /> tokens cost at these many layers, plus the footer's reserve, and never under the three-layer cap.</summary>
    public static int TraceByteCapFor(int layerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layerCount, MaxTraceLayers);
        return Math.Max(MinTraceByteCap, (layerCount * MaxNewTokens * TraceBytesPerTokenLayer * TraceCapMargin) + TraceFooterReserveBytes);
    }

    /// <summary>A lens line per <see cref="MaxNewTokens" /> step at its widest (every block's fields and a text for each of its top tokens), plus the lens footer's own reserve.</summary>
    public static int LensByteCapFor(int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockCount, MaxTraceLayers);
        return (MaxNewTokens * (LensBytesPerLine + (blockCount * (LensBytesPerBlock + (LensTopTokens * LensBytesPerText))))) + TraceFooterReserveBytes;
    }

    /// <summary>Three layers spread across the stack. Quarter points, so block 0, which cannot be steered, is not one of them.</summary>
    public static IReadOnlyList<int> LayersFor(int blockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        return new[]
        {
            blockCount / 4,
            blockCount / 2,
            blockCount * 3 / 4
        }.Distinct().ToArray();
    }

    /// <summary>The full-attention block nearest to about 0.6 depth, the measured steering depth; any block near there when the config names no kinds.</summary>
    /// <param name="steerableBlocks">Blocks a vector can reach; block 0 is not among the candidates, because a vector cannot target it.</param>
    /// <param name="layerTypes">The checkpoint's <c>layer_types</c>, or null.</param>
    public static int DefaultSteeringLayer(int steerableBlocks, IReadOnlyList<string>? layerTypes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steerableBlocks, 2);
        var target = steerableBlocks * DefaultSteeringDepth;
        var candidates = Enumerable.Range(1, steerableBlocks - 1).ToArray();
        var full = layerTypes is null
            ? []
            : candidates.Where(layer => layer < layerTypes.Count && !string.Equals(layerTypes[layer], LinearAttention, StringComparison.Ordinal)).ToArray();
        return (full.Length > 0 ? full : candidates).MinBy(layer => Math.Abs(layer - target));
    }

    /// <summary>A saved result, or <see langword="null" /> when the row's bytes do not read as one.</summary>
    public static ModelLabTraceResult? ReadResultOrNull(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<ModelLabTraceResult>(json.Span, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static ModelLabTrace ParseTrace(ReadOnlyMemory<byte> content)
    {
        return ParseLinesOrNull<ModelLabTraceRecord>(content) is { } lines
            ? new ModelLabTrace
            {
                Status = ModelLabTraceReadStatus.Found,
                Records = lines.Records,
                Footer = lines.Footer
            }
            : new ModelLabTrace
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    /// <summary>Unavailable as well when a row does not have one key list, weight list and rest per head of <paramref name="heads" />.</summary>
    internal static ModelLabAttention ParseAttention(ReadOnlyMemory<byte> content, int heads)
    {
        return ParseLinesOrNull<ModelLabAttentionRecord>(content) is { } lines && lines.Records.TrueForAll(record => IsWellFormed(record, heads))
            ? new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.Found,
                Records = lines.Records,
                Footer = lines.Footer
            }
            : new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    /// <summary>A trace file's lens lines, skipping every other line. Unavailable when a line does not parse or the lines fail the checks below.</summary>
    /// <remarks>
    ///     The steps run 0, 1, 2, ... in order; their count matches the summary and the lens footer, and is <paramref name="expectedTokens" /> or, when that
    ///     footer says the cap cut them, fewer. The summary's blocks are 0 to B-1 and its truncated flag is the footer's. Every block has the top-token count, a
    ///     rank of at least 1 (null exactly where the chosen logprob is), an entropy that is null or not negative, and a text for every top id.
    /// </remarks>
    internal static ModelLabLens ParseLens(ReadOnlyMemory<byte> content, ModelLabLensSummary summary, int expectedTokens)
    {
        var records = new List<ModelLabLensRecord>();
        ModelLabTraceFooter? footer = null;
        try
        {
            foreach (var line in Lines(content))
            {
                if (line.StartsWith(LensFooterPrefix, StringComparison.Ordinal))
                {
                    footer = JsonSerializer.Deserialize<LensFooterLine>(line, JsonSerializerOptions.Default)?.LensFooter;
                }
                else if (line.StartsWith(LensLinePrefix, StringComparison.Ordinal))
                {
                    records.Add(JsonSerializer.Deserialize<LensLine>(line, JsonSerializerOptions.Default)?.Lens ?? throw new JsonException("A lens line without its record."));
                }
            }
        }
        catch (JsonException)
        {
            return new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        }

        var readable = footer is { } lensFooter
                       && summary is { Layers.Count: > 0, TopTokens: > 0 }
                       && summary.Layers.SequenceEqual(Enumerable.Range(0, summary.Layers.Count))
                       && summary.Truncated == lensFooter.Truncated
                       && records.Count == summary.Records
                       && records.Count == lensFooter.Records
                       && (records.Count == expectedTokens || (records.Count < expectedTokens && lensFooter.Truncated))
                       && records.Select(static record => record.Step).SequenceEqual(Enumerable.Range(0, records.Count))
                       && records.TrueForAll(record => IsWellFormed(record, summary.Layers.Count, summary.TopTokens));
        return readable
            ? new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.Found,
                Summary = summary,
                Records = records,
                Footer = footer
            }
            : new ModelLabLens
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    private static bool IsWellFormed(ModelLabLensRecord record, int blocks, int topTokens) =>
        record is { Ids: { } ids, Logprobs: { } logprobs, ChosenLogprobs: { } chosen, ChosenRanks: { } ranks, Entropies: { } entropies, Texts: { } texts }
        && ids.Count == blocks
        && logprobs.Count == blocks
        && chosen.Count == blocks
        && ranks.Count == blocks
        && entropies.Count == blocks
        && ids.All(block => block is { } top && top.Count == topTokens && top.All(id => id >= 0 && texts.GetValueOrDefault(id.ToString(CultureInfo.InvariantCulture)) is not null))
        && logprobs.All(block => block?.Count == topTokens)
        && ranks.Zip(chosen).All(static block => (block.First is null) == (block.Second is null) && block.First is null or >= 1)
        && entropies.All(static entropy => entropy is null or >= 0);

    // JSON null deserializes into the non-nullable lists, so each is checked: a malformed row is unreadable, not a crash.
    private static bool IsWellFormed(ModelLabAttentionRecord record, int heads) =>
        record is { Keys: { } keys, Weights: { } weights, Rest: { } rest }
        && keys.Count == heads
        && weights.Count == heads
        && rest.Count == heads
        && keys.Zip(weights).All(static head => head is { First: { } headKeys, Second: { } headWeights } && headKeys.Count == headWeights.Count);

    private static string[] Lines(ReadOnlyMemory<byte> content) =>
        Encoding.UTF8.GetString(content.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The JSON lines both capped files share: records, then the writer's footer. Lens lines and their footer are skipped, never read as a default record. Null when a line does not parse.</summary>
    private static ParsedLines<T>? ParseLinesOrNull<T>(ReadOnlyMemory<byte> content)
    {
        var records = new List<T>();
        ModelLabTraceFooter? footer = null;
        foreach (var line in Lines(content))
        {
            try
            {
                if (line.StartsWith(LensLinePrefix, StringComparison.Ordinal) || line.StartsWith(LensFooterPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("{\"footer\"", StringComparison.Ordinal))
                {
                    footer = JsonSerializer.Deserialize<ModelLabTraceFooter>(line, JsonSerializerOptions.Default);
                }
                else if (JsonSerializer.Deserialize<T>(line, JsonSerializerOptions.Default) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // The blob's hash was verified, so this is a script bug, not tampering; the file is shown as unreadable.
                return null;
            }
        }

        return new ParsedLines<T>
        {
            Records = records,
            Footer = footer
        };
    }

    private sealed class LensLine
    {
        [JsonPropertyName("lens")]
        public ModelLabLensRecord? Lens { get; init; }
    }

    private sealed class LensFooterLine
    {
        [JsonPropertyName("lensFooter")]
        public ModelLabTraceFooter? LensFooter { get; init; }
    }

    private sealed class ParsedLines<T>
    {
        public required List<T> Records { get; init; }

        public required ModelLabTraceFooter? Footer { get; init; }
    }
}
