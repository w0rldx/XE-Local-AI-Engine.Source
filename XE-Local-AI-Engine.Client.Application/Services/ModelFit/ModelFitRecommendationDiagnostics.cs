namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The typed read of one recommendation row's persisted diagnostics blob: the only fields the recommendation view
///     surfaces, so the rest of the blob stays server-side.
/// </summary>
/// <remarks>
///     Tolerant field by field: a null, blank or malformed blob, a non-object root, a missing key and a value of the
///     wrong JSON kind all read as the field's default. A row written before a key shipped therefore reads as that
///     default, never as an error. Written by <c>ModelFitRefreshService</c> and <see cref="RecommendationJsonParser" />.
/// </remarks>
public sealed class ModelFitRecommendationDiagnostics
{
    private ModelFitRecommendationDiagnostics()
    {
    }

    public string? ReleaseDate { get; private init; }

    /// <summary>An explicit <c>is_trusted_publisher</c> boolean wins; otherwise trust is derived from the model name.</summary>
    /// <remarks>
    ///     The fallback covers rows persisted before the advisor emitted the signal, so pre-existing snapshots are not
    ///     all silently flagged untrusted until the next refresh regenerates the blob.
    /// </remarks>
    public bool IsTrustedPublisher { get; private init; }

    /// <summary>Catalog-lane section; a row written before the catalog lane defaults to <c>explore</c>.</summary>
    public string Section { get; private init; } = "explore";

    public string? Tier { get; private init; }

    public bool Tested { get; private init; }

    /// <summary>Whether the repository is gated; a row written before the advisor emitted the flag reads false.</summary>
    public bool IsGated { get; private init; }

    public string? CatalogId { get; private init; }

    public string? CatalogDisplayName { get; private init; }

    public string? CatalogNotes { get; private init; }

    public bool ExpertsOffloaded { get; private init; }

    public double? GpuGb { get; private init; }

    public double? CpuGb { get; private init; }

    /// <summary>Advisory-only quantized-KV estimate; absent for explore rows and pre-advisory snapshots. Never drives fit or ranking.</summary>
    public string? KvQuant { get; private init; }

    public double? KvQuantEstimatedGb { get; private init; }

    public double? KvQuantHeadroomGb { get; private init; }

    public bool? KvQuantFits { get; private init; }

    public bool? KvQuantRequiresFlashAttention { get; private init; }

    /// <summary>KV cost per token of context at the snapshot's context target; a fractional or out-of-range number reads as null.</summary>
    public long? KvBytesPerToken { get; private init; }

    public string? KvBytesPerTokenQuant { get; private init; }

    public string? AttentionArch { get; private init; }

    /// <summary>Parses <paramref name="diagnosticsJson" /> once. Never throws for malformed input.</summary>
    public static ModelFitRecommendationDiagnostics Parse(string? diagnosticsJson, string modelName)
    {
        var fallback = new ModelFitRecommendationDiagnostics
        {
            IsTrustedPublisher = GgufPublisherTrust.IsTrustedPublisher(modelName)
        };

        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }

            return new ModelFitRecommendationDiagnostics
            {
                ReleaseDate = ReadString(root, "release_date"),
                IsTrustedPublisher = ReadBool(root, "is_trusted_publisher") ?? fallback.IsTrustedPublisher,
                Section = ReadString(root, "section") ?? fallback.Section,
                Tier = ReadString(root, "tier"),
                Tested = ReadBool(root, "tested") ?? false,
                IsGated = ReadBool(root, "is_gated") ?? false,
                CatalogId = ReadString(root, "catalog_id"),
                CatalogDisplayName = ReadString(root, "catalog_display_name"),
                CatalogNotes = ReadString(root, "catalog_notes"),
                ExpertsOffloaded = ReadBool(root, "expert_offload") ?? false,
                GpuGb = ReadDouble(root, "gpu_gb"),
                CpuGb = ReadDouble(root, "cpu_gb"),
                KvQuant = ReadString(root, "kv_quant"),
                KvQuantEstimatedGb = ReadDouble(root, "kv_quant_estimated_gb"),
                KvQuantHeadroomGb = ReadDouble(root, "kv_quant_headroom_gb"),
                KvQuantFits = ReadBool(root, "kv_quant_fits"),
                KvQuantRequiresFlashAttention = ReadBool(root, "kv_quant_requires_flash_attention"),
                KvBytesPerToken = root.TryGetProperty("kv_bytes_per_token", out var kvBytes)
                                  && kvBytes.ValueKind == JsonValueKind.Number
                                  && kvBytes.TryGetInt64(out var perToken)
                    ? perToken
                    : null,
                KvBytesPerTokenQuant = ReadString(root, "kv_bytes_per_token_quant"),
                AttentionArch = ReadString(root, "attention_arch")
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? ReadBool(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;
    }

    private static double? ReadDouble(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }
}
