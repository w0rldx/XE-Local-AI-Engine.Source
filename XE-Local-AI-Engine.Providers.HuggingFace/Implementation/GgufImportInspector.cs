namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class GgufImportInspector(HuggingFaceOptions options) : IGgufImportInspector
{
    private static readonly HashSet<string> CausalArchitectures = new(StringComparer.Ordinal)
    {
        "llama", "mistral", "mixtral", "qwen2", "qwen2moe", "qwen3", "qwen3moe", "gemma", "gemma2", "gemma3",
        "phi2", "phi3", "phi3moe", "deepseek2", "command-r", "cohere2", "gpt2", "gptneox", "starcoder2", "internlm2"
    };

    public async Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var displayName = Path.GetFileName(source.AbsolutePath) ?? string.Empty;
        try
        {
            await using var opened = ValidatedGgufImportSource.Open(source.AbsolutePath, options.ModelsDirectory);
            return await InspectOpenedAsync(opened, cancellationToken).ConfigureAwait(false);
        }
        catch (GgufImportException)
        {
            return Rejected(displayName, size: 0, GgufImportRejectionCode.InvalidSource);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Rejected(displayName, size: 0, GgufImportRejectionCode.InvalidSource);
        }
    }

    internal static async Task<GgufImportInspection> InspectOpenedAsync(ValidatedGgufImportSource source,
        CancellationToken cancellationToken)
    {
        var header = await GgufStrictHeaderParser.ReadAsync(source.Stream, cancellationToken).ConfigureAwait(false);
        source.Rewind();
        return Classify(source.DisplayName, source.Length, header) with { SourceIdentityToken = source.SourceIdentityToken };
    }

    internal static GgufImportInspection Classify(string displayName, long size, GgufStrictHeaderParser.StrictHeader header)
    {
        var rejections = new List<GgufImportRejectionCode>();
        if (header.Version is null || !header.IsComplete)
        {
            rejections.Add(GgufImportRejectionCode.InvalidGguf);
        }
        else if (header.Version is < 2 or > 3)
        {
            rejections.Add(GgufImportRejectionCode.UnsupportedVersion);
        }

        if (header.TryGetInt64("split.count", out var splitCount) && splitCount > 1
            || header.TryGetInt64("split.no", out _)
            || IsShardName(displayName))
        {
            rejections.Add(GgufImportRejectionCode.SplitModel);
        }

        var type = header.GetString("general.type");
        if (type is not null && !string.Equals(type, "model", StringComparison.Ordinal))
        {
            rejections.Add(GgufImportRejectionCode.UnsupportedModelType);
        }

        var architecture = NormalizeArchitecture(header.GetString("general.architecture"));
        if (architecture is null || !CausalArchitectures.Contains(architecture)
            || IsRejectedArchitecture(architecture, displayName))
        {
            rejections.Add(GgufImportRejectionCode.UnsupportedArchitecture);
        }

        var quant = GgufQuantDetector.Detect(displayName, header);
        if (quant is null)
        {
            rejections.Add(header.Values.ContainsKey("general.file_type")
                ? GgufImportRejectionCode.UnsupportedQuantization
                : GgufImportRejectionCode.QuantizationRequired);
        }

        var workloadRejected = rejections.Any(static rejection => rejection is not GgufImportRejectionCode.QuantizationRequired
                                                                  and not GgufImportRejectionCode.UnsupportedQuantization);
        return new GgufImportInspection(size,
            header.Version,
            architecture,
            workloadRejected ? null : GgufImportWorkload.CausalChat,
            quant,
            displayName,
            rejections.Distinct().ToArray(),
            []);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "GGUF architecture identifiers are externally defined lowercase tokens and the inspection contract preserves that canonical form.")]
    private static string? NormalizeArchitecture(string? architecture) => architecture?.Trim().ToLowerInvariant();

    private static bool IsShardName(string displayName)
    {
        var stem = Path.GetFileNameWithoutExtension(displayName);
        var ofMarker = stem.LastIndexOf("-of-", StringComparison.OrdinalIgnoreCase);
        if (ofMarker <= 0 || ofMarker + 4 >= stem.Length)
        {
            return false;
        }

        var leftDash = stem.LastIndexOf('-', ofMarker - 1);
        return leftDash >= 0
               && int.TryParse(stem.AsSpan(leftDash + 1, ofMarker - leftDash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out _)
               && int.TryParse(stem.AsSpan(ofMarker + 4), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsRejectedArchitecture(string architecture, string displayName)
    {
        return architecture.Contains("bert", StringComparison.Ordinal)
               || displayName.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("projector", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("adapter", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("embed", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("rerank", StringComparison.OrdinalIgnoreCase);
    }

    private static GgufImportInspection Rejected(string displayName, long size, GgufImportRejectionCode code)
    {
        return new GgufImportInspection(size, null, null, null, null, displayName, [code], []);
    }
}
