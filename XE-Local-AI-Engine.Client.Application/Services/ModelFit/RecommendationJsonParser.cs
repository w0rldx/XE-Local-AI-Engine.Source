namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Tolerant parser for llmfit <c>recommend --json</c> output (schema captured live, 2026-06-02). It maps the
///     top-level <c>{ "models": [...], "system": {...} }</c> shape to normalized recommendation rows plus a sanitized
///     <c>system</c> diagnostics blob. Tolerant by design: unknown fields are ignored, missing/null fields map to null,
///     and a malformed root (not an object, or <c>models</c> absent / not an array) is a typed failure — never an
///     exception out of the parser. Empty <c>models: []</c> is a SUCCESS with zero rows.
/// </summary>
public static class RecommendationJsonParser
{
    /// <summary>
    ///     Parses <paramref name="standardOutput" />. On success returns the ranked rows (rank = array order, 1-based)
    ///     and the serialized <c>system</c> object as snapshot diagnostics. On a parse failure returns
    ///     <see cref="RecommendationParseResult.Failure" />. Never throws for malformed input.
    /// </summary>
    public static RecommendationParseResult Parse(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return RecommendationParseResult.Failure();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(standardOutput);
        }
        catch (JsonException)
        {
            return RecommendationParseResult.Failure();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("models", out var modelsElement)
                || modelsElement.ValueKind != JsonValueKind.Array)
            {
                return RecommendationParseResult.Failure();
            }

            var recommendations = new List<ModelFitRecommendationInput>(modelsElement.GetArrayLength());
            var rank = 0;
            foreach (var model in modelsElement.EnumerateArray())
            {
                rank++;
                if (model.ValueKind != JsonValueKind.Object)
                {
                    // A non-object array element is treated as a skipped (unparseable) entry rather than a hard failure:
                    // tolerant parsing keeps the rest of a partially-valid payload usable.
                    continue;
                }

                recommendations.Add(MapModel(model, rank));
            }

            var systemDiagnostics = root.TryGetProperty("system", out var systemElement)
                                    && systemElement.ValueKind == JsonValueKind.Object
                ? systemElement.GetRawText()
                : null;

            return RecommendationParseResult.Success(recommendations, systemDiagnostics);
        }
    }

    private static ModelFitRecommendationInput MapModel(JsonElement model, int rank)
    {
        // ollama_name is the legacy llmfit provider tag; repo_id/file_name are the advisor's GGUF identity. Prefer the
        // GGUF repo id as the pull/provider name when present, else the legacy ollama tag (tolerant to both shapes).
        var ollamaName = GetString(model, "ollama_name");
        var repoId = GetString(model, "repo_id");
        var providerModelName = repoId ?? ollamaName;
        var memoryRequiredGb = GetDouble(model, "memory_required_gb");
        var vramRequiredGb = GetDouble(model, "vram_required_gb");

        return new ModelFitRecommendationInput(rank,
            GetString(model, "name") ?? string.Empty,
            providerModelName,
            GetDouble(model, "score") ?? 0d,
            GetString(model, "fit_level"),
            GetString(model, "run_mode"),
            GetString(model, "best_quant"),
            GetDouble(model, "estimated_tps"),
            // memory_required_gb is in GB; the column is MB.
            memoryRequiredGb is { } gb ? gb * 1024d : null,
            // The advisor fills vram_required_gb from the memory-fit estimate (null for a CPU-mode fit / legacy payload).
            vramRequiredGb is { } vramGb ? vramGb * 1024d : null,
            // Context column = the model's advertised maximum window (context_length). effective_context_length is
            // llmfit's memory-estimation cap (defaults to 8192 when --max-context / OLLAMA_CONTEXT_LENGTH are unset), so
            // preferring it showed 8192 for every model; fall back to it only when context_length is absent.
            GetInt(model, "context_length") ?? GetInt(model, "effective_context_length"),
            GetBool(model, "installed") ?? false,
            // The advisor's model name IS the GGUF registry pull key ({repo}:{quant}); fall back to the legacy ollama tag.
            GetString(model, "name") ?? ollamaName,
            BuildDiagnostics(model));
    }

    /// <summary>Builds a small sanitized JSON of extras kept for the UI/audit. Returns <c>null</c> when there is nothing to keep.</summary>
    private static string? BuildDiagnostics(JsonElement model)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var wroteAny = false;
            wroteAny |= CopyProperty(model, "category", writer);
            wroteAny |= CopyProperty(model, "score_components", writer);
            wroteAny |= CopyProperty(model, "is_moe", writer);
            wroteAny |= CopyProperty(model, "params_b", writer);
            // release_date and is_trusted_publisher ride the existing (already-persisted) diagnostics blob so the read
            // mapper can surface them as "newer model" and "publisher trust" signals without new columns/migrations.
            wroteAny |= CopyProperty(model, "release_date", writer);
            wroteAny |= CopyProperty(model, "is_trusted_publisher", writer);
            // Catalog-lane fields (curated catalog, explore section, MoE expert offload) ride the same diagnostics
            // extensibility seam — additive, no new columns. Absent for a pre-existing (explore-only) snapshot row.
            wroteAny |= CopyProperty(model, "section", writer);
            wroteAny |= CopyProperty(model, "tier", writer);
            wroteAny |= CopyProperty(model, "catalog_id", writer);
            wroteAny |= CopyProperty(model, "catalog_display_name", writer);
            wroteAny |= CopyProperty(model, "catalog_notes", writer);
            wroteAny |= CopyProperty(model, "expert_offload", writer);
            wroteAny |= CopyProperty(model, "gpu_gb", writer);
            wroteAny |= CopyProperty(model, "cpu_gb", writer);
            // Advisory-only quantized-KV estimate (catalog lane) rides the same additive diagnostics seam. Advisory only:
            // the default chat launch uses an fp16 KV cache, so these fields never drive fit/ranking — they let the UI hint
            // at the headroom a flash-attention runtime could unlock. Absent for explore-lane / insufficient-metadata rows.
            wroteAny |= CopyProperty(model, "kv_quant", writer);
            wroteAny |= CopyProperty(model, "kv_quant_estimated_gb", writer);
            wroteAny |= CopyProperty(model, "kv_quant_headroom_gb", writer);
            wroteAny |= CopyProperty(model, "kv_quant_fits", writer);
            wroteAny |= CopyProperty(model, "kv_quant_requires_flash_attention", writer);
            // KV cost per token at the run's context target plus the attention shape, on the same additive seam.
            // Presentation and a ranking tiebreak's companion — neither drives membership.
            wroteAny |= CopyProperty(model, "kv_bytes_per_token", writer);
            wroteAny |= CopyProperty(model, "kv_bytes_per_token_quant", writer);
            wroteAny |= CopyProperty(model, "attention_arch", writer);
            writer.WriteEndObject();

            if (!wroteAny)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool CopyProperty(JsonElement model, string propertyName, Utf8JsonWriter writer)
    {
        if (!model.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        writer.WritePropertyName(propertyName);
        value.WriteTo(writer);
        return true;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }
}

/// <summary>
///     Result of parsing recommendation JSON. <see cref="IsSuccess" /> distinguishes a parse failure (malformed root /
///     missing models array) from a successful parse — note an empty model list is a SUCCESS with zero rows.
/// </summary>
public sealed record RecommendationParseResult
{
    private RecommendationParseResult(bool isSuccess,
        IReadOnlyList<ModelFitRecommendationInput> recommendations,
        string? systemDiagnosticsJson)
    {
        IsSuccess = isSuccess;
        Recommendations = recommendations;
        SystemDiagnosticsJson = systemDiagnosticsJson;
    }

    public bool IsSuccess { get; }

    public IReadOnlyList<ModelFitRecommendationInput> Recommendations { get; }

    public string? SystemDiagnosticsJson { get; }

    public static RecommendationParseResult Success(IReadOnlyList<ModelFitRecommendationInput> recommendations,
        string? systemDiagnosticsJson)
    {
        return new RecommendationParseResult(isSuccess: true, recommendations, systemDiagnosticsJson);
    }

    public static RecommendationParseResult Failure()
    {
        return new RecommendationParseResult(isSuccess: false, [], systemDiagnosticsJson: null);
    }
}
