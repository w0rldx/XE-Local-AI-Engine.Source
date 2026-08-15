namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A downloaded (or downloading) base checkpoint as the application layer sees it. The two JSON documents are
///     carried as <see cref="ReadOnlyMemory{T}" /> rather than arrays so the record cannot hand a caller a mutable
///     reference to the decrypted column contents.
/// </summary>
public sealed record TrainingBaseArtifactRecord(
    Guid Id,
    string RepoId,
    string Revision,
    TrainingBaseArtifactStatus Status,
    ReadOnlyMemory<byte> FilesJson,
    long TotalBytes,
    ReadOnlyMemory<byte>? LicenseJson,
    string? ErrorMessage,
    long Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>Raised when a mutation's expected version does not match the row's current one.</summary>
public sealed class TrainingBaseArtifactConcurrencyException : Exception
{
    public TrainingBaseArtifactConcurrencyException()
    {
    }

    public TrainingBaseArtifactConcurrencyException(string message)
        : base(message)
    {
    }

    public TrainingBaseArtifactConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Persistence boundary for <see cref="TrainingBaseArtifact" />. Every mutation takes the version it expects and
///     compares before writing, following the benchmark store's manually-bumped concurrency-token pattern.
/// </summary>
public interface ITrainingBaseArtifactStore
{
    /// <summary>
    ///     Starts a download for <paramref name="repoId" /> at <paramref name="revision" />.
    /// </summary>
    /// <remarks>
    ///     <c>(repo_id, revision)</c> is UNIQUE, so a previously failed download of the same checkpoint would block a
    ///     second insert. Retrying therefore RESETS the existing row back to <c>Downloading</c> (clearing its error and
    ///     manifest) rather than inserting a second one. A row already in <c>Downloading</c> is returned untouched so a
    ///     double-submit does not restart an in-flight transfer, and an existing <c>Ready</c> row is returned as-is.
    /// </remarks>
    Task<TrainingBaseArtifactRecord> StartDownloadAsync(string repoId, string revision, CancellationToken cancellationToken = default);

    Task<TrainingBaseArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrainingBaseArtifactRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<TrainingBaseArtifactRecord> MarkReadyAsync(Guid artifactId,
        long expectedVersion,
        byte[] filesJson,
        long totalBytes,
        byte[]? licenseJson,
        CancellationToken cancellationToken = default);

    Task<TrainingBaseArtifactRecord> MarkFailedAsync(Guid artifactId,
        long expectedVersion,
        string errorMessage,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the resolved commit the download was actually pinned to, once the Hub has answered.
    /// </summary>
    Task<TrainingBaseArtifactRecord> SetRevisionAsync(Guid artifactId,
        long expectedVersion,
        string revision,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the row. Returns false when it does not exist.</summary>
    Task<bool> DeleteAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Terminalizes rows left <c>Downloading</c> by a process that died mid-transfer. A download has no work-item
    ///     row to claim, so nothing else would ever move them off <c>Downloading</c> and the delete guard would refuse
    ///     forever. Returns how many rows were terminalized.
    /// </summary>
    Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default);
}
