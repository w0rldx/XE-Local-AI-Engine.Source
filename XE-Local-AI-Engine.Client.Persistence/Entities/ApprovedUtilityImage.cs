namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A code-seeded, operator-governable approved utility image descriptor (e.g. the pinned llmfit image). Keyed by a
///     stable string id with <c>NOCASE</c> collation. All columns are plaintext — image references, names and sanitized
///     diagnostics are not secrets. The <see cref="ImageReference" /> is code/migration-owned: it is mutated only by the
///     code-seed step, never from an API or DTO, so the registry can never be pointed at an unapproved image at runtime.
///     Operators may toggle <see cref="Enabled" />; that toggle is preserved across re-seeds.
/// </summary>
internal sealed record class ApprovedUtilityImage
{
    /// <summary>Stable descriptor id (primary key, <c>NOCASE</c> collation). Code-owned. Plaintext.</summary>
    public string ApprovedImageId { get; set; } = string.Empty;

    /// <summary>Operator-facing display name. Plaintext.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional longer description. Plaintext.</summary>
    public string? Description { get; set; }

    /// <summary>The model-fit operations this image is sanctioned for (bit flags). Plaintext.</summary>
    public UtilityImagePurpose Purpose { get; set; }

    /// <summary>
    ///     Canonical <c>repository:tag@sha256:&lt;digest&gt;</c> image reference. Code/migration-owned — never settable from
    ///     any API or DTO. Plaintext.
    /// </summary>
    public string ImageReference { get; set; } = string.Empty;

    /// <summary>Optional upstream project URL. Plaintext.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Optional upstream version label (e.g. <c>0.9.30</c>). Plaintext.</summary>
    public string? UpstreamVersion { get; set; }

    /// <summary>Operator gate: an approved descriptor ships disabled and is flipped on by an operator. Plaintext.</summary>
    public bool Enabled { get; set; }

    /// <summary>Unix-ms instant this descriptor was deprecated, or null while current. Plaintext.</summary>
    public long? DeprecatedAtUtc { get; set; }

    /// <summary>Id of the descriptor that replaces this one when deprecated, or null. Plaintext.</summary>
    public string? ReplacementApprovedImageId { get; set; }

    /// <summary>Unix-ms instant the row was first created. Plaintext.</summary>
    public long CreatedAtUtc { get; set; }

    /// <summary>Unix-ms instant of the last row write. Plaintext.</summary>
    public long UpdatedAtUtc { get; set; }

    /// <summary>Unix-ms instant a run last used this image, or null. Plaintext.</summary>
    public long? LastUsedAtUtc { get; set; }

    /// <summary>Unix-ms instant a run using this image last succeeded, or null. Plaintext.</summary>
    public long? LastSuccessfulRunAtUtc { get; set; }

    /// <summary>Optional sanitized JSON of provenance/license/review notes. Plaintext (sanitized, not secret).</summary>
    public string? DiagnosticsJson { get; set; }
}
