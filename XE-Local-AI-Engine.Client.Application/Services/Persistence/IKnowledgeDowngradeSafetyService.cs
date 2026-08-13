namespace XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     Inspects whether collection-scoped knowledge-document identities fit the legacy global content-hash uniqueness
///     and creates an operator-requested database snapshot before a downgrade.
/// </summary>
public interface IKnowledgeDowngradeSafetyService
{
    /// <summary>
    ///     Performs a read-only compatibility check. Conflict identifiers are deterministic and opaque; document content,
    ///     paths, names, source identifiers, and content hashes are never returned.
    /// </summary>
    Task<KnowledgeDowngradePreflightResult> PreflightAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runs the preflight and writes a consistent SQLite snapshot beneath the node data directory. The destination is
    ///     generated internally, never accepted from an operator-supplied path, and is never overwritten.
    /// </summary>
    Task<KnowledgeDowngradeExportResult> ExportAsync(CancellationToken cancellationToken = default);
}

/// <summary>Read-only downgrade compatibility report.</summary>
public sealed record KnowledgeDowngradePreflightResult(
    bool CollectionMigrationApplied,
    bool IsCompatible,
    int ConflictGroupCount,
    int ConflictingDocumentCount,
    int MinimumDocumentsToRemove,
    IReadOnlyList<KnowledgeDowngradeConflict> Conflicts);

/// <summary>A duplicate legacy hash group described only by opaque document identifiers.</summary>
public sealed record KnowledgeDowngradeConflict(string ConflictId, IReadOnlyList<string> DocumentIdentifiers);

/// <summary>Explicit database export plus the compatibility report captured immediately before it.</summary>
public sealed record KnowledgeDowngradeExportResult(
    string ArtifactPath,
    long ArtifactBytes,
    string ArtifactSha256,
    KnowledgeDowngradePreflightResult Preflight);
