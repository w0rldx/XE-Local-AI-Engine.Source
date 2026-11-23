namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>What every job runs. Fixed or bounded here so no request can widen what a job measures or stores past these limits.</summary>
public static class ModelLabTraceSpec
{
    /// <summary>Separate from the trainer's: the two scripts change independently. Pinned by <c>modellablib.CONTRACT_VERSION</c>.</summary>
    public const int ContractVersion = 8;

    /// <summary>What a trace generates when its request names no count, and what every steering arm generates.</summary>
    public const int MaxNewTokens = 128;

    /// <summary>The most tokens a trace request may ask for. Each byte cap below scales with the job's own count.</summary>
    public const int MaxRequestedNewTokens = 256;

    public const int Seed = 1234;

    /// <summary>A request may trace every block of the deepest checkpoint the lab loads; each layer costs about as much generation time as the rest.</summary>
    public const int MaxTraceLayers = 64;

    /// <summary>A trace result that would pass 0.9 MiB has its texts cut to 64 escaped bytes, then its last steps' candidates dropped (<c>modellablib.fitted_trace_result</c>).</summary>
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

    /// <summary>The most experts a router may pick per token for its choices to be recorded (<c>modellablib.MAX_ROUTED_EXPERTS_PER_TOKEN</c>).</summary>
    public const int MaxRoutedExpertsPerToken = 16;

    /// <summary>The most experts a recorded router may choose from (<c>modellablib.MAX_EXPERTS</c>).</summary>
    public const int MaxRoutedExperts = 1024;

    /// <summary>The routing file's weight encoding: each weight's bfloat16 bit pattern, so the stored value is exactly what the experts consumed.</summary>
    public const string RoutingWeightEncoding = "bfloat16-bits";

    /// <summary>bfloat16 1.0: a router weight is a probability, so no stored pattern lies above it.</summary>
    private const int MaxRoutingWeightBits = 0x3F80;

    /// <summary><c>{"routing":{"s":255,"x":[</c>, <c>],"w":[</c>, <c>]}}</c> and the newline.</summary>
    private const int RoutingBytesPerLine = 36;

    /// <summary>Worst-case widths at the most experts per token: ids of 4 digits and bit patterns of 5, each with a comma, plus both lists' brackets and separating commas.</summary>
    private const int RoutingBytesPerBlock = (11 * MaxRoutedExpertsPerToken) + 4;

    private const string RoutingLinePrefix = "{\"routing\"";

    private const string RoutingFooterPrefix = "{\"routingFooter\"";

    /// <summary>The largest file a trace can write: every layer traced, a lens and routing at every block, at the most tokens; with <see cref="MaxAttentionByteCap" />, the bound on what the blob store keeps.</summary>
    public static readonly int MaxTraceByteCap = TraceByteCapFor(MaxTraceLayers, MaxRequestedNewTokens) + LensByteCapFor(MaxTraceLayers, MaxRequestedNewTokens)
                                                 + RoutingByteCapFor(MaxTraceLayers, MaxRequestedNewTokens);

    /// <summary>The largest attention cap a checkpoint can ask for: about 40 MiB.</summary>
    public static readonly int MaxAttentionByteCap = AttentionByteCapFor(MaxAttentionLayers, MaxAttentionHeads, MaxRequestedNewTokens);

    /// <summary>Twice what every head's top keys cost at these many layers for <paramref name="tokens" /> rows, plus the footer's reserve.</summary>
    public static int AttentionByteCapFor(int layerCount, int heads, int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layerCount, MaxAttentionLayers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heads);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(heads, MaxAttentionHeads);
        ThrowUnlessTokenCount(tokens);
        return (layerCount * heads * tokens * AttentionTopKeys * AttentionBytesPerKey * AttentionCapMargin) + TraceFooterReserveBytes;
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

    /// <summary>Four times what <paramref name="tokens" /> tokens cost at these many layers, plus the footer's reserve, and never under the three-layer cap.</summary>
    public static int TraceByteCapFor(int layerCount, int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layerCount, MaxTraceLayers);
        ThrowUnlessTokenCount(tokens);
        return Math.Max(MinTraceByteCap, (layerCount * tokens * TraceBytesPerTokenLayer * TraceCapMargin) + TraceFooterReserveBytes);
    }

    /// <summary>A lens line per step of <paramref name="tokens" /> at its widest (every block's fields and a text for each of its top tokens), plus the lens footer's own reserve.</summary>
    public static int LensByteCapFor(int blockCount, int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockCount, MaxTraceLayers);
        ThrowUnlessTokenCount(tokens);
        return (tokens * (LensBytesPerLine + (blockCount * (LensBytesPerBlock + (LensTopTokens * LensBytesPerText))))) + TraceFooterReserveBytes;
    }

    /// <summary>A routing line per step of <paramref name="tokens" /> at its widest (every MoE block at the most experts per token), plus the routing footer's own reserve.</summary>
    public static int RoutingByteCapFor(int blockCount, int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockCount, MaxTraceLayers);
        ThrowUnlessTokenCount(tokens);
        return (tokens * (RoutingBytesPerLine + (blockCount * RoutingBytesPerBlock))) + TraceFooterReserveBytes;
    }

    private static void ThrowUnlessTokenCount(int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokens, MaxRequestedNewTokens);
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

    /// <summary>A trace file's routing lines, skipping every other line. Unavailable when a line does not parse or the lines fail the checks below.</summary>
    /// <remarks>
    ///     The summary names the bfloat16 encoding, distinct blocks below <paramref name="blockCount" />, 1 to 1,024 experts and 1 to 16 picks per token, at
    ///     most the experts. Exactly one routing footer, whose kept and dropped steps add up to <paramref name="generated" /> and whose counts the summary
    ///     repeats. The steps run 0, 1, 2, ... in order; every step has, per block, the picks' distinct ids below the expert count and their bit patterns
    ///     from 0 to bfloat16 1.0. The weights are not checked to sum to 1: an unnormalized router's picks hold only part of the probability.
    /// </remarks>
    internal static ModelLabRouting ParseRouting(ReadOnlyMemory<byte> content, ModelLabRoutingSummary summary, int blockCount, int generated)
    {
        var records = new List<ModelLabRoutingRecord>();
        var footers = new List<ModelLabTraceFooter?>();
        try
        {
            foreach (var line in Lines(content))
            {
                if (line.StartsWith(RoutingFooterPrefix, StringComparison.Ordinal))
                {
                    footers.Add(JsonSerializer.Deserialize<RoutingFooterLine>(line, JsonSerializerOptions.Default)?.RoutingFooter);
                }
                else if (line.StartsWith(RoutingLinePrefix, StringComparison.Ordinal))
                {
                    records.Add(JsonSerializer.Deserialize<RoutingLine>(line, JsonSerializerOptions.Default)?.Routing ?? throw new JsonException("A routing line without its record."));
                }
            }
        }
        catch (JsonException)
        {
            return new ModelLabRouting
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        }

        var readable = footers is [{ } footer]
                       && string.Equals(summary.WeightEncoding, RoutingWeightEncoding, StringComparison.Ordinal)
                       && summary is { Layers.Count: > 0, Experts: >= 1 and <= MaxRoutedExperts, ExpertsPerToken: >= 1 and <= MaxRoutedExpertsPerToken }
                       && summary.ExpertsPerToken <= summary.Experts
                       && summary.Layers.Distinct().Count() == summary.Layers.Count
                       && summary.Layers.All(layer => layer >= 0 && layer < blockCount)
                       && footer.Dropped >= 0
                       && footer.Truncated == footer.Dropped > 0
                       && footer.Records + footer.Dropped == generated
                       && summary.Records == footer.Records
                       && summary.Truncated == footer.Truncated
                       && records.Count == footer.Records
                       && records.Select(static record => record.Step).SequenceEqual(Enumerable.Range(0, records.Count))
                       && records.TrueForAll(record => IsWellFormed(record, summary.Layers.Count, summary.ExpertsPerToken, summary.Experts));
        return readable
            ? new ModelLabRouting
            {
                Status = ModelLabTraceReadStatus.Found,
                Summary = summary,
                Records = records,
                Footer = footers[0]
            }
            : new ModelLabRouting
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    /// <summary>A stored bit pattern's weight, exactly: a bfloat16 is the top half of a float.</summary>
    public static float RoutingWeight(int bits) =>
        BitConverter.Int32BitsToSingle(bits << 16);

    private static bool IsWellFormed(ModelLabRoutingRecord record, int blocks, int picks, int experts) =>
        record is { Experts: { } ids, WeightBits: { } bits }
        && ids.Count == blocks
        && bits.Count == blocks
        && ids.All(block => block is { } chosen && chosen.Count == picks && chosen.All(id => id >= 0 && id < experts) && chosen.Distinct().Count() == picks)
        && bits.All(block => block is { } weights && weights.Count == picks && weights.All(static weight => weight is >= 0 and <= MaxRoutingWeightBits));

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

    /// <summary>The JSON lines both capped files share: records, then the writer's footer. Lens and routing lines and their footers are skipped, never read as a default record. Null when a line does not parse.</summary>
    private static ParsedLines<T>? ParseLinesOrNull<T>(ReadOnlyMemory<byte> content)
    {
        var records = new List<T>();
        ModelLabTraceFooter? footer = null;
        foreach (var line in Lines(content))
        {
            try
            {
                if (line.StartsWith(LensLinePrefix, StringComparison.Ordinal)
                    || line.StartsWith(LensFooterPrefix, StringComparison.Ordinal)
                    || line.StartsWith(RoutingLinePrefix, StringComparison.Ordinal)
                    || line.StartsWith(RoutingFooterPrefix, StringComparison.Ordinal))
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

    private sealed class RoutingLine
    {
        [JsonPropertyName("routing")]
        public ModelLabRoutingRecord? Routing { get; init; }
    }

    private sealed class RoutingFooterLine
    {
        [JsonPropertyName("routingFooter")]
        public ModelLabTraceFooter? RoutingFooter { get; init; }
    }

    private sealed class ParsedLines<T>
    {
        public required List<T> Records { get; init; }

        public required ModelLabTraceFooter? Footer { get; init; }
    }
}
