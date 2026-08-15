namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for downloaded base checkpoints. <c>files_json</c> and <c>license_json</c> are encrypted at
///     rest by the node encryption interceptors; everything this store filters or orders by (id, repo, revision, status)
///     is structural plaintext, so no query here can depend on a decrypted column.
/// </summary>
public sealed class TrainingBaseArtifactStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ITrainingBaseArtifactStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<TrainingBaseArtifactRecord> StartDownloadAsync(string repoId, string revision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentNullException.ThrowIfNull(revision);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var existing = await _dbContext.TrainingBaseArtifacts
                                       .FirstOrDefaultAsync(row => row.RepoId == repoId && row.Revision == revision, cancellationToken)
                                       .ConfigureAwait(false);

        if (existing is not null)
        {
            // Downloading: a double-submit must not restart a transfer already in flight.
            // Ready: nothing to redo — the caller gets the artifact it asked for.
            if (existing.Status != TrainingBaseArtifactStatus.Failed)
            {
                return ToRecord(existing);
            }

            // Failed: (repo_id, revision) is UNIQUE, so a retry resets this row instead of inserting a second one.
            existing.Status = TrainingBaseArtifactStatus.Downloading;
            existing.ErrorMessage = null;
            existing.FilesJson = [];
            existing.LicenseJson = null;
            existing.TotalBytes = 0;
            existing.Version++;
            existing.UpdatedAtUtc = now;
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(existing);
        }

        var entity = new TrainingBaseArtifact
        {
            Id = Guid.NewGuid(),
            RepoId = repoId,
            Revision = revision,
            Status = TrainingBaseArtifactStatus.Downloading,
            FilesJson = [],
            TotalBytes = 0,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.TrainingBaseArtifacts.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task<TrainingBaseArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.TrainingBaseArtifacts
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(row => row.Id == artifactId, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<TrainingBaseArtifactRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.TrainingBaseArtifacts
                                       .AsNoTracking()
                                       .OrderByDescending(row => row.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<TrainingBaseArtifactRecord> MarkReadyAsync(Guid artifactId,
        long expectedVersion,
        byte[] filesJson,
        long totalBytes,
        byte[]? licenseJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filesJson);

        var entity = await LoadForUpdateAsync(artifactId, expectedVersion, cancellationToken).ConfigureAwait(false);
        entity.Status = TrainingBaseArtifactStatus.Ready;
        entity.FilesJson = filesJson;
        entity.TotalBytes = totalBytes;
        entity.LicenseJson = licenseJson;
        entity.ErrorMessage = null;
        return await SaveAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingBaseArtifactRecord> MarkFailedAsync(Guid artifactId,
        long expectedVersion,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);

        var entity = await LoadForUpdateAsync(artifactId, expectedVersion, cancellationToken).ConfigureAwait(false);
        entity.Status = TrainingBaseArtifactStatus.Failed;
        entity.ErrorMessage = errorMessage;
        return await SaveAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingBaseArtifactRecord> SetRevisionAsync(Guid artifactId,
        long expectedVersion,
        string revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);

        var entity = await LoadForUpdateAsync(artifactId, expectedVersion, cancellationToken).ConfigureAwait(false);
        entity.Revision = revision;
        return await SaveAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.TrainingBaseArtifacts
                                     .FirstOrDefaultAsync(row => row.Id == artifactId, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        if (entity.Version != expectedVersion)
        {
            throw new TrainingBaseArtifactConcurrencyException("The base checkpoint was modified by another operation. Reload and try again.");
        }

        _ = _dbContext.TrainingBaseArtifacts.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default)
    {
        var stranded = await _dbContext.TrainingBaseArtifacts
                                       .Where(row => row.Status == TrainingBaseArtifactStatus.Downloading)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        if (stranded.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var entity in stranded)
        {
            entity.Status = TrainingBaseArtifactStatus.Failed;
            entity.ErrorMessage = "The download was interrupted when the application stopped. Start it again to resume.";
            entity.Version++;
            entity.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return stranded.Count;
    }

    private async Task<TrainingBaseArtifact> LoadForUpdateAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.TrainingBaseArtifacts
                                     .FirstOrDefaultAsync(row => row.Id == artifactId, cancellationToken)
                                     .ConfigureAwait(false)
                     ?? throw new TrainingBaseArtifactConcurrencyException("The base checkpoint no longer exists.");

        if (entity.Version != expectedVersion)
        {
            throw new TrainingBaseArtifactConcurrencyException("The base checkpoint was modified by another operation. Reload and try again.");
        }

        return entity;
    }

    private async Task<TrainingBaseArtifactRecord> SaveAsync(TrainingBaseArtifact entity, CancellationToken cancellationToken)
    {
        entity.Version++;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    private static TrainingBaseArtifactRecord ToRecord(TrainingBaseArtifact entity)
    {
        return new TrainingBaseArtifactRecord(entity.Id,
            entity.RepoId,
            entity.Revision,
            entity.Status,
            entity.FilesJson,
            entity.TotalBytes,
            entity.LicenseJson,
            entity.ErrorMessage,
            entity.Version,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
    }
}
