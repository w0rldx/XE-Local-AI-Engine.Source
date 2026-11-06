namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text;
using System.Text.Json;

/// <summary>What every job runs. Fixed or bounded here so no request can widen what a job measures or stores past these limits.</summary>
public static class ModelLabTraceSpec
{
    /// <summary>Separate from the trainer's: the two scripts change independently. Pinned by <c>modellablib.CONTRACT_VERSION</c>.</summary>
    public const int ContractVersion = 2;

    public const int MaxNewTokens = 128;

    public const int Seed = 1234;

    /// <summary>A request may trace every block of the deepest checkpoint the lab loads; each layer costs about as much generation time as the rest.</summary>
    public const int MaxTraceLayers = 64;

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

    private const string LinearAttention = "linear_attention";

    /// <summary>Measured on a trace: one token's record at one layer is about 162 bytes.</summary>
    private const int TraceBytesPerTokenLayer = 162;

    private const int TraceCapMargin = 4;

    /// <summary>The script's room for its footer line, <c>modellablib.FOOTER_RESERVE_BYTES</c>.</summary>
    private const int TraceFooterReserveBytes = 160;

    /// <summary>The cap from when every trace had three layers. No layer count gets less, so no trace that fitted then is refused now.</summary>
    private const int MinTraceByteCap = 256 * 1024;

    /// <summary>The largest cap a request can ask for, so the bound on what the blob store keeps.</summary>
    public static readonly int MaxTraceByteCap = TraceByteCapFor(MaxTraceLayers);

    /// <summary>Four times what <see cref="MaxNewTokens" /> tokens cost at these many layers, plus the footer's reserve, and never under the three-layer cap.</summary>
    public static int TraceByteCapFor(int layerCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layerCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layerCount, MaxTraceLayers);
        return Math.Max(MinTraceByteCap, (layerCount * MaxNewTokens * TraceBytesPerTokenLayer * TraceCapMargin) + TraceFooterReserveBytes);
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
        var records = new List<ModelLabTraceRecord>();
        ModelLabTraceFooter? footer = null;
        foreach (var line in Encoding.UTF8.GetString(content.Span).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (line.StartsWith("{\"footer\"", StringComparison.Ordinal))
                {
                    footer = JsonSerializer.Deserialize<ModelLabTraceFooter>(line, JsonSerializerOptions.Default);
                }
                else if (JsonSerializer.Deserialize<ModelLabTraceRecord>(line, JsonSerializerOptions.Default) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // The blob's hash was verified, so this is a script bug, not tampering; the trace is shown as unreadable.
                return new ModelLabTrace
                {
                    Status = ModelLabTraceReadStatus.Unavailable
                };
            }
        }

        return new ModelLabTrace
        {
            Status = ModelLabTraceReadStatus.Found,
            Records = records,
            Footer = footer
        };
    }
}
