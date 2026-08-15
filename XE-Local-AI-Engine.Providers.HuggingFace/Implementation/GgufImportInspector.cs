namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class GgufImportInspector(HuggingFaceOptions options) : IGgufImportInspector
{
    private static readonly HashSet<string> CausalArchitectures = new(StringComparer.Ordinal)
    {
        "llama",
        "mistral",
        "mixtral",
        "qwen2",
        "qwen2moe",
        "qwen3",
        "qwen3moe",
        "qwen35",
        "qwen35moe",
        "gemma",
        "gemma2",
        "gemma3",
        "phi2",
        "phi3",
        "phi3moe",
        "deepseek2",
        "command-r",
        "cohere2",
        "gpt2",
        "gptneox",
        "starcoder2",
        "internlm2"
    };

    /// <inheritdoc />
    public Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken) =>
        InspectAsync(source, GgufImportInspectionMode.PublicImport, cancellationToken);

    public async Task<GgufImportInspection> InspectAsync(GgufImportSource source,
        GgufImportInspectionMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var displayName = Path.GetFileName(source.AbsolutePath) ?? string.Empty;
        try
        {
            await using var opened = ValidatedGgufImportSource.Open(source.AbsolutePath, options.ModelsDirectory);
            return await InspectOpenedAsync(opened, mode, cancellationToken).ConfigureAwait(false);
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
        GgufImportInspectionMode mode,
        CancellationToken cancellationToken)
    {
        var header = await GgufStrictHeaderParser.ReadAsync(source.Stream, cancellationToken).ConfigureAwait(false);
        source.Rewind();
        return Classify(source.DisplayName, source.Length, header, mode) with
        {
            SourceIdentityToken = source.SourceIdentityToken
        };
    }

    internal static GgufImportInspection Classify(string displayName,
        long size,
        GgufStrictHeaderParser.StrictHeader header,
        GgufImportInspectionMode mode = GgufImportInspectionMode.PublicImport)
    {
        var inProcess = mode == GgufImportInspectionMode.InProcessTrainedCommit;
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

        // "adapter" is a first-class type on the in-process path — that is what a LoRA export produces — and stays a
        // rejection on the public one, where an operator has no legitimate reason to upload a bare adapter.
        var type = header.GetString("general.type");
        var isAdapter = inProcess && string.Equals(type, "adapter", StringComparison.Ordinal);
        if (type is not null && !isAdapter && !string.Equals(type, "model", StringComparison.Ordinal))
        {
            rejections.Add(GgufImportRejectionCode.UnsupportedModelType);
        }

        // The display-name substring checks are a public-surface heuristic against a mislabelled upload. An in-process
        // commit is a file the engine just wrote and whose type it read directly, so the name carries no evidence —
        // notably, a merged fine-tune of a model whose own name contains "adapter" must not be rejected for it.
        var architecture = NormalizeArchitecture(header.GetString("general.architecture"));
        if (architecture is null || !CausalArchitectures.Contains(architecture)
                                 || !inProcess && IsRejectedArchitecture(architecture, displayName)
                                 || inProcess && architecture.Contains("bert", StringComparison.Ordinal))
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
        var workload = isAdapter ? GgufImportWorkload.LoraAdapter : GgufImportWorkload.CausalChat;
        return new GgufImportInspection(size,
            header.Version,
            architecture,
            workloadRejected ? null : workload,
            quant,
            displayName,
            rejections.Distinct().ToArray(),
            []);
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "GGUF architecture identifiers are externally defined lowercase tokens and the inspection contract preserves that canonical form.")]
    private static string? NormalizeArchitecture(string? architecture) =>
        architecture?.Trim().ToLowerInvariant();

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
