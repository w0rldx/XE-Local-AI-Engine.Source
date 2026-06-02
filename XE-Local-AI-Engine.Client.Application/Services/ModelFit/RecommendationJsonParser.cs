namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Tolerant parser for llmfit <c>recommend --json</c> output (schema captured live at plan Marker 0). It maps the
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
        var ollamaName = GetString(model, "ollama_name");
        var memoryRequiredGb = GetDouble(model, "memory_required_gb");

        return new ModelFitRecommendationInput(
            Rank: rank,
            ModelName: GetString(model, "name") ?? string.Empty,
            ProviderModelName: ollamaName,
            Score: GetDouble(model, "score") ?? 0d,
            FitLevel: GetString(model, "fit_level"),
            RunMode: GetString(model, "run_mode"),
            Quantization: GetString(model, "best_quant"),
            EstimatedTokensPerSecond: GetDouble(model, "estimated_tps"),
            // memory_required_gb is in GB; the column is MB.
            RequiredRamMb: memoryRequiredGb is { } gb ? gb * 1024d : null,
            // No per-model VRAM field exists separately from memory_required_gb.
            RequiredVramMb: null,
            ContextTokens: GetInt(model, "effective_context_length") ?? GetInt(model, "context_length"),
            IsInstalled: GetBool(model, "installed") ?? false,
            PullModelName: ollamaName,
            DiagnosticsJson: BuildDiagnostics(model));
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
            writer.WriteEndObject();

            if (!wroteAny)
            {
                return null;
            }
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;

    private static int? GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>
///     Result of parsing recommendation JSON. <see cref="IsSuccess" /> distinguishes a parse failure (malformed root /
///     missing models array) from a successful parse — note an empty model list is a SUCCESS with zero rows.
/// </summary>
public sealed record RecommendationParseResult
{
    private RecommendationParseResult(
        bool isSuccess,
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

    public static RecommendationParseResult Success(
        IReadOnlyList<ModelFitRecommendationInput> recommendations,
        string? systemDiagnosticsJson) =>
        new(true, recommendations, systemDiagnosticsJson);

    public static RecommendationParseResult Failure() =>
        new(false, [], null);
}
