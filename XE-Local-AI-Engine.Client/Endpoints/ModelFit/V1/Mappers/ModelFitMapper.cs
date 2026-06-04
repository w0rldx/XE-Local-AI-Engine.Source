namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Extension methods that translate the application-layer model-fit records into sanitized endpoint DTOs. This is the
///     sole point in the Client project that references those record member names. Every projection is sanitized: the
///     recommendation view never carries raw output / stderr / diagnostics.
/// </summary>
internal static class ModelFitMapper
{
    // -----------------------------------------------------------------------
    // Approved image record → response
    // -----------------------------------------------------------------------

    public static ApprovedImageResponse ToResponse(this ApprovedUtilityImageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ApprovedImageResponse
        {
            ApprovedImageId = record.ApprovedImageId,
            DisplayName = record.DisplayName,
            Description = record.Description,
            Purpose = MapPurpose(record.Purpose),
            ImageReference = record.ImageReference,
            SourceUrl = record.SourceUrl,
            UpstreamVersion = record.UpstreamVersion,
            Enabled = record.Enabled,
            DeprecatedAtUtc = record.DeprecatedAtUtc,
            ReplacementApprovedImageId = record.ReplacementApprovedImageId,
            LastUsedAtUtc = record.LastUsedAtUtc,
            LastSuccessfulRunAtUtc = record.LastSuccessfulRunAtUtc,
            Diagnostics = record.DiagnosticsJson
        };
    }

    // -----------------------------------------------------------------------
    // Latest recommendations view → response
    // -----------------------------------------------------------------------

    public static GetLatestRecommendationsResponse ToResponse(this ModelFitLatestRecommendationsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new GetLatestRecommendationsResponse
        {
            HasCache = true,
            SnapshotId = view.SnapshotId,
            Status = view.Status.ToString(),
            SourceImageId = view.ApprovedImageId,
            UseCase = view.UseCase,
            ProviderName = view.ProviderName,
            LastRefreshedAtUtc = view.CompletedAtUtc,
            Recommendations = [.. view.Recommendations.Select(static r => r.ToResponse())]
        };
    }

    /// <summary>The explicit cache-miss response: no cached recommendation snapshot exists for the key.</summary>
    public static GetLatestRecommendationsResponse EmptyCache() =>
        new()
        {
            HasCache = false,
            SnapshotId = null,
            Status = null,
            SourceImageId = null,
            UseCase = null,
            ProviderName = null,
            LastRefreshedAtUtc = null,
            Recommendations = []
        };

    private static ModelFitRecommendationResponse ToResponse(this ModelFitRecommendationRecord record) =>
        new()
        {
            Rank = record.Rank,
            ModelName = record.ModelName,
            ProviderModelName = record.ProviderModelName,
            Score = record.Score,
            FitLevel = record.FitLevel,
            RunMode = record.RunMode,
            Quantization = record.Quantization,
            EstimatedTokensPerSecond = record.EstimatedTokensPerSecond,
            RequiredRamMb = record.RequiredRamMb,
            RequiredVramMb = record.RequiredVramMb,
            ContextTokens = record.ContextTokens,
            IsInstalled = record.IsInstalled,
            PullModelName = record.PullModelName,
            ReleaseDate = ExtractReleaseDate(record.DiagnosticsJson)
        };

    /// <summary>
    ///     Pulls ONLY the <c>release_date</c> string out of the persisted diagnostics blob (the rest stays server-side, so
    ///     the row projection remains sanitized). Tolerant: a null/blank/malformed blob, a non-object root, or a missing /
    ///     non-string <c>release_date</c> all yield <c>null</c>.
    /// </summary>
    private static string? ExtractReleaseDate(string? diagnosticsJson)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("release_date", out var releaseDate)
                   && releaseDate.ValueKind == JsonValueKind.String
                ? releaseDate.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> MapPurpose(UtilityImagePurpose purpose)
    {
        var purposes = new List<string>(2);

        if (purpose.HasFlag(UtilityImagePurpose.ModelRecommendation))
        {
            purposes.Add(nameof(UtilityImagePurpose.ModelRecommendation));
        }

        if (purpose.HasFlag(UtilityImagePurpose.ModelBenchmark))
        {
            purposes.Add(nameof(UtilityImagePurpose.ModelBenchmark));
        }

        return purposes;
    }
}
