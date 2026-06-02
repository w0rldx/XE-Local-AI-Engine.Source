namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for the approved utility image registry. All columns are plaintext (image references,
///     names and sanitized diagnostics are not secrets). This store performs no validation — that is the
///     application-layer validator's responsibility; the store only maps rows to records. The store deliberately exposes
///     NO method to change <c>image_reference</c>: it is code/migration-owned and mutated only by
///     <see cref="UpsertSeedAsync" />, so the registry can never be pointed at an unapproved image at runtime.
/// </summary>
public interface IApprovedUtilityImageStore
{
    /// <summary>Returns every approved image descriptor, ordered by <c>ApprovedImageId</c>.</summary>
    Task<IReadOnlyList<ApprovedUtilityImageRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the descriptor for <paramref name="approvedImageId" /> (case-insensitive), or <c>null</c> when none exists.</summary>
    Task<ApprovedUtilityImageRecord?> GetByIdAsync(string approvedImageId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts or updates a code-seeded descriptor. On insert the row is written as supplied. On update the code-owned
    ///     fields (display name, description, purpose, image reference, source url, upstream version, deprecation,
    ///     replacement, diagnostics) are refreshed, but the operator-set <c>Enabled</c> toggle and the original
    ///     <c>CreatedAtUtc</c>/usage timestamps are preserved. Returns the stored record.
    /// </summary>
    Task<ApprovedUtilityImageRecord> UpsertSeedAsync(ApprovedUtilityImageRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets <c>Enabled</c> on the descriptor with <paramref name="approvedImageId" /> and bumps <c>UpdatedAtUtc</c>.
    ///     Returns the updated record, or <c>null</c> when none exists.
    /// </summary>
    Task<ApprovedUtilityImageRecord?> SetEnabledAsync(string approvedImageId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stamps <c>LastUsedAtUtc</c> (and, when supplied, <c>LastSuccessfulRunAtUtc</c>) on the descriptor with
    ///     <paramref name="approvedImageId" /> and bumps <c>UpdatedAtUtc</c>. Returns the updated record, or <c>null</c>
    ///     when none exists.
    /// </summary>
    Task<ApprovedUtilityImageRecord?> TouchUsedAsync(
        string approvedImageId,
        long lastUsedAtUtc,
        long? lastSuccessfulRunAtUtc = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Typed projection of a persisted approved utility image descriptor. All fields are plaintext.
/// </summary>
public sealed record ApprovedUtilityImageRecord(
    string ApprovedImageId,
    string DisplayName,
    string? Description,
    UtilityImagePurpose Purpose,
    string ImageReference,
    string? SourceUrl,
    string? UpstreamVersion,
    bool Enabled,
    long? DeprecatedAtUtc,
    string? ReplacementApprovedImageId,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long? LastUsedAtUtc,
    long? LastSuccessfulRunAtUtc,
    string? DiagnosticsJson);
