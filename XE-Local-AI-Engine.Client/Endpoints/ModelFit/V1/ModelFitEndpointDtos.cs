namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

// ---------------------------------------------------------------------------
// Approved image response DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Wire projection of an approved utility image descriptor. Surfaces only the read-only descriptor metadata —
///     <c>Purpose</c> is mapped from the <c>[Flags]</c> enum to a string list, and <c>Diagnostics</c> is already-sanitized
///     metadata. The image reference is code/seed-owned and read-only; no field on this DTO is editable from the client.
/// </summary>
public sealed class ApprovedImageResponse
{
    public required string ApprovedImageId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>The sanctioned purposes (e.g. <c>ModelRecommendation</c>, <c>ModelBenchmark</c>) projected from the flags enum.</summary>
    public required IReadOnlyList<string> Purpose { get; init; }

    public required string ImageReference { get; init; }

    public string? SourceUrl { get; init; }

    public string? UpstreamVersion { get; init; }

    public required bool Enabled { get; init; }

    public long? DeprecatedAtUtc { get; init; }

    public string? ReplacementApprovedImageId { get; init; }

    public long? LastUsedAtUtc { get; init; }

    public long? LastSuccessfulRunAtUtc { get; init; }

    /// <summary>Already-sanitized diagnostics metadata (plaintext); null when none was recorded.</summary>
    public string? Diagnostics { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/approved-images</c>.</summary>
public sealed class ListApprovedImagesResponse
{
    public required IReadOnlyList<ApprovedImageResponse> Items { get; init; }
}

// ---------------------------------------------------------------------------
// Latest recommendations request/response DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Query-string request for <c>GET model-fit/recommendations/latest</c>. <see cref="UseCase" /> is optional;
///     <see cref="ProviderName" /> defaults to <c>ollama</c> when omitted. Neither carries any raw image reference or
///     command — only the cache-lookup key.
/// </summary>
public sealed class GetLatestRecommendationsRequest
{
    /// <summary>Optional use-case filter for the cached recommendation key (null matches the use-case-less recommendation snapshot).</summary>
    public string? UseCase { get; init; }

    /// <summary>Provider name for the cached recommendation key; defaults to <c>ollama</c> when the query param is omitted.</summary>
    public string ProviderName { get; init; } = "ollama";
}

/// <summary>
///     Sanitized projection of one ranked recommendation row. Carries only normalized model metadata — NOTHING from the
///     snapshot's raw output, stderr excerpt or detailed diagnostics.
/// </summary>
public sealed class ModelFitRecommendationResponse
{
    public required int Rank { get; init; }

    public required string ModelName { get; init; }

    public string? ProviderModelName { get; init; }

    public required double Score { get; init; }

    public string? FitLevel { get; init; }

    public string? RunMode { get; init; }

    public string? Quantization { get; init; }

    public double? EstimatedTokensPerSecond { get; init; }

    public double? RequiredRamMb { get; init; }

    public double? RequiredVramMb { get; init; }

    public int? ContextTokens { get; init; }

    public required bool IsInstalled { get; init; }

    public string? PullModelName { get; init; }
}

/// <summary>
///     Response for <c>GET model-fit/recommendations/latest</c>. The response is ALWAYS 200 with an explicit
///     <see cref="HasCache" /> flag rather than a 404, so the UI can distinguish "no recommendation has ever been cached"
///     (an empty/diagnostics state) from a transport error. When <see cref="HasCache" /> is <c>false</c> every snapshot
///     field is <c>null</c> and <see cref="Recommendations" /> is empty. The payload exposes only the sanitized snapshot
///     summary plus the normalized rows — never any raw output, stderr or diagnostics.
/// </summary>
public sealed class GetLatestRecommendationsResponse
{
    /// <summary>True when a cached recommendation snapshot exists for the key; false on a cache-miss (the empty state).</summary>
    public required bool HasCache { get; init; }

    public Guid? SnapshotId { get; init; }

    /// <summary>The snapshot run status string name (e.g. <c>Succeeded</c>); null on a cache-miss.</summary>
    public string? Status { get; init; }

    public string? SourceImageId { get; init; }

    public string? UseCase { get; init; }

    public string? ProviderName { get; init; }

    /// <summary>Unix-ms instant the cached snapshot completed; null on a cache-miss.</summary>
    public long? LastRefreshedAtUtc { get; init; }

    public required IReadOnlyList<ModelFitRecommendationResponse> Recommendations { get; init; }
}

// ---------------------------------------------------------------------------
// Refresh request/response DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST model-fit/recommendations/refresh</c>. Carries ONLY the id of an existing scheduled job to fire —
///     never an image reference, command line or template id. The service self-guards that the job is a
///     <c>model-recommendation-check</c> job, so this endpoint can never fire an arbitrary scheduled job.
/// </summary>
public sealed class RefreshRecommendationsRequest
{
    public Guid ScheduledJobId { get; init; }
}

/// <summary>
///     Accepted response for <c>POST model-fit/recommendations/refresh</c>. The refresh is created asynchronously by the
///     scheduler (the run id is owned by the scheduler dispatcher, so it is NOT fabricated here); the response only
///     echoes the scheduled job id that was triggered.
/// </summary>
public sealed class RefreshRecommendationsResponse
{
    public required Guid ScheduledJobId { get; init; }
}
