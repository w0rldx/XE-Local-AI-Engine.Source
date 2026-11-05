namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text;
using System.Text.Json;

/// <summary>What every job runs. Fixed here so no request can widen what a job measures or stores.</summary>
public static class ModelLabTraceSpec
{
    /// <summary>Separate from the trainer's: the two scripts change independently. Pinned by <c>modellablib.CONTRACT_VERSION</c>.</summary>
    public const int ContractVersion = 2;

    public const int MaxNewTokens = 128;

    public const int Seed = 1234;

    /// <summary>About four times what 128 tokens cost at three layers (measured: about 162 bytes per token per layer).</summary>
    public const int TraceByteCap = 256 * 1024;

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
