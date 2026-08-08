namespace XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;

/// <summary>
///     Validates a candidate catalog JSON document — the schema-version gate + per-field checks every bundled and
///     remote-refreshed catalog must pass before it can replace the served snapshot. Tolerant of parse failures (never
///     throws for malformed input) but strict on content: a document with any invalid entry is rejected wholesale rather
///     than silently dropping the bad rows, so a corrupt remote payload can never partially poison recommendations.
/// </summary>
public static class ModelCatalogValidator
{
    /// <summary>The only <see cref="ModelCatalogDocument.SchemaVersion" /> this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Parses and validates <paramref name="rawJson" />. Never throws — a parse failure is a validation failure.</summary>
    public static ModelCatalogValidationResult Validate(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return ModelCatalogValidationResult.Failure(["Catalog JSON is empty."]);
        }

        ModelCatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ModelCatalogDocument>(rawJson, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return ModelCatalogValidationResult.Failure([$"Catalog JSON could not be parsed: {exception.Message}"]);
        }

        if (document is null)
        {
            return ModelCatalogValidationResult.Failure(["Catalog JSON deserialized to null."]);
        }

        var errors = new List<string>();

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add($"Unsupported schemaVersion {document.SchemaVersion} (expected {SupportedSchemaVersion}).");
        }

        if (string.IsNullOrWhiteSpace(document.CatalogVersion))
        {
            errors.Add("catalogVersion is required.");
        }

        if (document.Models is null)
        {
            errors.Add("models array is required.");
        }
        else
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < document.Models.Count; index++)
            {
                ValidateEntry(document.Models[index], index, seenIds, errors);
            }
        }

        return errors.Count == 0 ? ModelCatalogValidationResult.Success(document) : ModelCatalogValidationResult.Failure(errors);
    }

    private static void ValidateEntry(ModelCatalogEntry? entry, int index, HashSet<string> seenIds, List<string> errors)
    {
        var prefix = $"models[{index}]";

        if (entry is null)
        {
            errors.Add($"{prefix} is null.");
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            errors.Add($"{prefix}.id is required.");
        }
        else if (!seenIds.Add(entry.Id))
        {
            errors.Add($"{prefix}.id '{entry.Id}' is a duplicate.");
        }

        if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            errors.Add($"{prefix}.displayName is required.");
        }

        if (string.IsNullOrWhiteSpace(entry.GgufRepo) || !entry.GgufRepo.Contains('/', StringComparison.Ordinal))
        {
            errors.Add($"{prefix}.ggufRepo must be an 'owner/repo' Hugging Face repository id.");
        }

        if (entry.Tier is not ("S" or "A" or "B"))
        {
            errors.Add($"{prefix}.tier must be one of S, A, B.");
        }

        if (entry.UseCases is null
            || entry.UseCases.Count == 0
            || entry.UseCases.Any(useCase => !ModelFitRequestValidator.AllowedUseCases.Contains(useCase)))
        {
            errors.Add($"{prefix}.useCases must be a non-empty list drawn from {string.Join(", ", ModelFitRequestValidator.AllowedUseCases)}.");
        }

        if (entry.TotalParamsB <= 0)
        {
            errors.Add($"{prefix}.totalParamsB must be positive.");
        }

        if (entry.Moe && entry.ActiveParamsB is not > 0)
        {
            errors.Add($"{prefix}.activeParamsB must be positive when moe is true.");
        }

        if (entry.ActiveParamsB is { } active && active > entry.TotalParamsB)
        {
            errors.Add($"{prefix}.activeParamsB cannot exceed totalParamsB.");
        }

        if (entry.ContextLength <= 0)
        {
            errors.Add($"{prefix}.contextLength must be positive.");
        }

        if (ModelCatalogArchGate.ParseBNumber(entry.MinLlamaCppTag) is null)
        {
            errors.Add($"{prefix}.minLlamaCppTag must be a 'bNNNN' llama.cpp release tag.");
        }

        if (!DateOnly.TryParseExact(entry.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            errors.Add($"{prefix}.releaseDate must be an ISO date (yyyy-MM-dd).");
        }
    }
}
