namespace XE_Local_AI_Engine.Client.Services.Images.Catalog;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Validates a candidate image-model catalog document. Tolerant of parse failures (never throws) but strict on
///     content: one invalid entry rejects the whole document, so a bad edit can never surface a catalog row whose
///     one-click install would 404, escape the models directory, or silently disable the free-disk pre-flight.
/// </summary>
public static class ImageModelCatalogValidator
{
    /// <summary>The only <see cref="ImageModelCatalogDocument.SchemaVersion" /> this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Parses and validates <paramref name="rawJson" />. Never throws — a parse failure is a validation failure.</summary>
    public static ImageModelCatalogValidationResult Validate(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return ImageModelCatalogValidationResult.Failure(["Catalog JSON is empty."]);
        }

        ImageModelCatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ImageModelCatalogDocument>(rawJson, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return ImageModelCatalogValidationResult.Failure([$"Catalog JSON could not be parsed: {exception.Message}"]);
        }

        if (document is null)
        {
            return ImageModelCatalogValidationResult.Failure(["Catalog JSON deserialized to null."]);
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

        return errors.Count == 0 ? ImageModelCatalogValidationResult.Success(document) : ImageModelCatalogValidationResult.Failure(errors);
    }

    private static void ValidateEntry(ImageModelCatalogEntry? entry, int index, HashSet<string> seenIds, List<string> errors)
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

        if (!IsRepoId(entry.RepoId))
        {
            errors.Add($"{prefix}.repoId must be an 'owner/repo' Hugging Face repository id.");
        }

        if (!Enum.TryParse<ImageModelFamily>(entry.Family, ignoreCase: true, out var family) || family == ImageModelFamily.Unknown)
        {
            errors.Add($"{prefix}.family must be a known image model family (Sd15, Sdxl, Sd3, Flux, QwenImage).");
        }

        if (entry.Parts is null || entry.Parts.Count == 0)
        {
            errors.Add($"{prefix}.parts must list at least the diffusion weight file.");
            return;
        }

        var seenRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasDiffusion = false;
        for (var partIndex = 0; partIndex < entry.Parts.Count; partIndex++)
        {
            hasDiffusion |= ValidatePart(entry.Parts[partIndex], $"{prefix}.parts[{partIndex}]", seenRoles, errors);
        }

        if (!hasDiffusion)
        {
            errors.Add($"{prefix}.parts must include exactly one Diffusion part.");
        }
    }

    // Returns whether this part is the diffusion part, so the caller can enforce that every set has one.
    private static bool ValidatePart(ImageModelCatalogPart? part, string prefix, HashSet<string> seenRoles, List<string> errors)
    {
        if (part is null)
        {
            errors.Add($"{prefix} is null.");
            return false;
        }

        var isDiffusion = false;
        if (!Enum.TryParse<ImageModelPartRole>(part.Role, ignoreCase: true, out var role))
        {
            errors.Add($"{prefix}.role '{part.Role}' is not a known part role.");
        }
        else
        {
            // One file per role: the launch argument builder emits one flag per role, so a duplicate would silently
            // drop a file the model needs.
            if (!seenRoles.Add(role.ToString()))
            {
                errors.Add($"{prefix}.role '{role}' appears more than once in the file-set.");
            }

            isDiffusion = role == ImageModelPartRole.Diffusion;
        }

        // The catalog is in-repo content, but it feeds the same download path an untrusted repo listing feeds, so it
        // passes the same containment guard rather than being trusted for being ours.
        if (!GgufFilePath.IsSafeRelativePath(part.FileName))
        {
            errors.Add($"{prefix}.fileName must be a safe repo-relative path.");
        }

        if (part.RepoId is not null && !IsRepoId(part.RepoId))
        {
            errors.Add($"{prefix}.repoId, when present, must be an 'owner/repo' Hugging Face repository id.");
        }

        // A missing/zero size is not merely cosmetic: it turns the pre-flight disk check into a no-op and makes the
        // set total incomputable, so the operator watches an 18 GB transfer with no percentage. Required here.
        if (part.SizeBytes <= 0)
        {
            errors.Add($"{prefix}.sizeBytes must be positive.");
        }

        return isDiffusion;
    }

    private static bool IsRepoId(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Contains('/', StringComparison.Ordinal);
    }
}
