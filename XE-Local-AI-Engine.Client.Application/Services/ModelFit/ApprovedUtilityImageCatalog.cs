namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     The code-defined source of truth for approved utility image descriptors. The registry is SQLite-backed but
///     seeded from this catalog on startup: the image reference is code/migration-owned and never
///     settable from any API. Each descriptor ships <c>Enabled = false</c> — an operator flips it on after review; the
///     seed preserves that operator toggle on re-seed. The timestamps on these records are placeholders; the store
///     stamps the real creation/update times.
/// </summary>
public static class ApprovedUtilityImageCatalog
{
    /// <summary>Stable id of the pinned llmfit recommender/benchmark image descriptor.</summary>
    public const string LlmfitRecommenderImageId = "llmfit-recommender-0-9-30";

    /// <summary>
    ///     Canonical, digest-pinned reference for the approved llmfit image (verified 2026-06-02). Code-owned;
    ///     this is the only place the reference is defined.
    /// </summary>
    public const string LlmfitImageReference =
        "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c";

    /// <summary>Sanitized provenance/license/review note stored as the descriptor diagnostics (no secrets).</summary>
    private const string LlmfitDiagnosticsJson =
        """{"license":"MIT","review":"operator-approved 2026-06-02, no CVE scan"}""";

    /// <summary>
    ///     Returns the code-defined approved image descriptors. Each ships disabled (operator-approved enablement) and
    ///     carries a sanitized provenance note. Timestamps are zero placeholders — the store assigns the real values.
    /// </summary>
    public static IReadOnlyList<ApprovedUtilityImageRecord> Descriptors { get; } =
    [
        new(ApprovedImageId: LlmfitRecommenderImageId,
            DisplayName: "llmfit recommender 0.9.30",
            Description: null,
            Purpose: UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark,
            ImageReference: LlmfitImageReference,
            // S1075: this is a fixed provenance reference (the upstream project), not a runtime endpoint or path — it is a
            // descriptor metadata value the operator sees, never dereferenced by the node.
#pragma warning disable S1075
            SourceUrl: "https://github.com/AlexsJones/llmfit",
#pragma warning restore S1075
            UpstreamVersion: "0.9.30",
            Enabled: false,
            DeprecatedAtUtc: null,
            ReplacementApprovedImageId: null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LastUsedAtUtc: null,
            LastSuccessfulRunAtUtc: null,
            DiagnosticsJson: LlmfitDiagnosticsJson)
    ];
}
