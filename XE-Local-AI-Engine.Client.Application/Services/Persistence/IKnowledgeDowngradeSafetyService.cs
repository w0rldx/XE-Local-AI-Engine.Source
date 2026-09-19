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
public sealed class KnowledgeDowngradePreflightResult
{
    public required bool CollectionMigrationApplied { get; init; }

    public required bool IsCompatible { get; init; }

    public required int ConflictGroupCount { get; init; }

    public required int ConflictingDocumentCount { get; init; }

    public required int MinimumDocumentsToRemove { get; init; }

    public required IReadOnlyList<KnowledgeDowngradeConflict> Conflicts { get; init; }
}

/// <summary>A duplicate legacy hash group described only by opaque document identifiers.</summary>
public sealed class KnowledgeDowngradeConflict
{
    public required string ConflictId { get; init; }

    public required IReadOnlyList<string> DocumentIdentifiers { get; init; }
}

/// <summary>Explicit database export plus the compatibility report captured immediately before it.</summary>
public sealed class KnowledgeDowngradeExportResult
{
    public required string ArtifactPath { get; init; }

    public required long ArtifactBytes { get; init; }

    public required string ArtifactSha256 { get; init; }

    public required KnowledgeDowngradePreflightResult Preflight { get; init; }
}
