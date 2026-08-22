namespace XE_Local_AI_Engine.Client.Services.Drafting;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Transport-neutral, bounded input for informational AI-drafting provenance.</summary>
public sealed record GenerationMetadataInput(
    string? Model,
    DraftMode Mode,
    string? UserBrief,
    string? Rationale,
    IReadOnlyList<string>? Assumptions,
    double Confidence,
    long GeneratedAtUtc,
    string? DraftContentHash);

/// <summary>Transport-neutral projection of persisted AI-drafting provenance.</summary>
public sealed record GenerationMetadataView(
    string? Model,
    DraftMode Mode,
    string? UserBrief,
    string? Rationale,
    IReadOnlyList<string> Assumptions,
    double Confidence,
    long GeneratedAtUtc,
    string? DraftContentHash,
    long AcceptedAtUtc,
    bool WasEdited);

/// <summary>The single validation and persistence authority shared by HTTP and MCP save transports.</summary>
public static class GenerationProvenance
{
    public const int MaxAssumptionLength = 300;
    public const int MaxAssumptions = 10;
    public const int MaxDraftContentHashLength = 64;
    public const int MaxModelLength = 200;
    public const int MaxRationaleLength = 2000;
    public const int MaxUserBriefLength = 4000;

    private static readonly JsonSerializerOptions PersistedOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public static string? Validate(GenerationMetadataInput? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        if (metadata.Model is { Length: > MaxModelLength })
        {
            return $"Generation metadata model must be at most {MaxModelLength} characters.";
        }

        if (metadata.UserBrief is { Length: > MaxUserBriefLength })
        {
            return $"Generation metadata brief must be at most {MaxUserBriefLength} characters.";
        }

        if (metadata.Rationale is { Length: > MaxRationaleLength })
        {
            return $"Generation metadata rationale must be at most {MaxRationaleLength} characters.";
        }

        if (metadata.DraftContentHash is { Length: > MaxDraftContentHashLength })
        {
            return $"Generation metadata draft content hash must be at most {MaxDraftContentHashLength} characters.";
        }

        if (metadata.Assumptions is { } assumptions)
        {
            if (assumptions.Count > MaxAssumptions)
            {
                return $"Generation metadata must carry at most {MaxAssumptions} assumptions.";
            }

            if (assumptions.Any(static assumption => assumption?.Length > MaxAssumptionLength))
            {
                return $"Each generation metadata assumption must be at most {MaxAssumptionLength} characters.";
            }
        }

        return !double.IsFinite(metadata.Confidence) || metadata.Confidence is < 0d or > 1d
            ? "Generation metadata confidence must be a number between 0 and 1."
            : null;
    }

    public static string? ToPersistedJson(GenerationMetadataInput? metadata,
        string? savedName,
        string? savedDescription,
        string? savedContent,
        DateTimeOffset acceptedAt)
    {
        if (metadata is null)
        {
            return null;
        }

        var savedHash = DraftContentHash.Compute(savedName, savedDescription, savedContent);
        var wasEdited = !string.Equals(savedHash, metadata.DraftContentHash, StringComparison.OrdinalIgnoreCase);
        return JsonSerializer.Serialize(new GenerationMetadataView(metadata.Model,
                metadata.Mode,
                metadata.UserBrief,
                metadata.Rationale,
                metadata.Assumptions ?? [],
                metadata.Confidence,
                metadata.GeneratedAtUtc,
                metadata.DraftContentHash,
                acceptedAt.ToUnixTimeMilliseconds(),
                wasEdited),
            PersistedOptions);
    }

    public static GenerationMetadataView? FromPersistedJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<GenerationMetadataView>(json, PersistedOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
