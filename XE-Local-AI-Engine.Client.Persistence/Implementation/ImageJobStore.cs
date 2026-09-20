namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     EF-backed <see cref="IImageJobStore" />. Writes go through <see cref="NodeChatDbContext.SaveChangesAsync" /> so the
///     node encryption interceptor encrypts the prompt / negative prompt at rest on insert; a status-only update leaves
///     the prompt property unmodified, so the interceptor skips it and the stored ciphertext is preserved. Reads use the
///     no-tracking path (the materialization interceptor decrypts the prompt columns either way). Scoped: one instance per
///     DI scope, matching the DbContext lifetime.
/// </summary>
public sealed class ImageJobStore : IImageJobStore
{
    private readonly NodeChatDbContext _dbContext;

    public ImageJobStore(NodeChatDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task CreateQueuedAsync(ImageJobCreate create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(create);

        var entity = new ImageJob
        {
            Id = create.Id,
            ModelName = create.ModelName,
            Prompt = Encoding.UTF8.GetBytes(create.Prompt),
            NegativePrompt = create.NegativePrompt is null ? null : Encoding.UTF8.GetBytes(create.NegativePrompt),
            Seed = create.Seed,
            Width = create.Width,
            Height = create.Height,
            Steps = create.Steps,
            Sampler = create.Sampler,
            CfgScale = create.CfgScale,
            Status = ImageJobStatus.Queued,
            CreatedAtUtc = create.CreatedAtUtc
        };

        _ = _dbContext.ImageJobs.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ImageJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var entity = await _dbContext.ImageJobs
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);

        return entity is null ? null : ToView(entity);
    }

    public async Task<IReadOnlyList<ImageJobView>> ListAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        // Floor both bounds: a negative limit reaches SQLite as LIMIT -1, which is "no limit" — the whole table, every
        // prompt decrypted, for a caller that asked for a page.
        var take = Math.Max(val1: 0, limit);
        var skip = Math.Max(val1: 0, offset);

        var entities = await _dbContext.ImageJobs
                                       .AsNoTracking()
                                       .OrderByDescending(job => job.CreatedAtUtc)
                                       .ThenByDescending(job => job.Id)
                                       .Skip(skip)
                                       .Take(take)
                                       .ToListAsync(cancellationToken);

        return entities.Select(ToView).ToArray();
    }

    public Task<int> CountAsync(CancellationToken cancellationToken)
    {
        return _dbContext.ImageJobs.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>?> DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(jobId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        // The declared ON DELETE CASCADE does fire on the node connection, but the delete is still explicit and
        // set-based: the storage paths have to be read before the rows go, or the blob teardown has nothing to unlink.
        // The two statements share one transaction so a job never survives its own images.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var storagePaths = await _dbContext.GeneratedImages
                                           .AsNoTracking()
                                           .Where(image => image.JobId == jobId)
                                           .Select(image => image.StoragePath)
                                           .ToArrayAsync(cancellationToken);

        _ = await _dbContext.GeneratedImages
                            .Where(image => image.JobId == jobId)
                            .ExecuteDeleteAsync(cancellationToken);

        _ = _dbContext.ImageJobs.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return storagePaths;
    }

    public async Task MarkGeneratingAsync(Guid jobId, long startedAtUtc, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(jobId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Status = ImageJobStatus.Generating;
        entity.StartedAtUtc = startedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSucceededAsync(Guid jobId,
        Guid imageId,
        long completedAtUtc,
        long durationMs,
        int outputWidth,
        int outputHeight,
        long resolvedSeed,
        CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(jobId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Status = ImageJobStatus.Succeeded;
        entity.ImageId = imageId;
        entity.CompletedAtUtc = completedAtUtc;
        entity.DurationMs = durationMs;

        // Replace the requested dimensions with the produced ones so the job row describes the PNG that exists. Guarded
        // against a non-positive value so a runtime that could not determine its output never blanks the row.
        if (outputWidth > 0 && outputHeight > 0)
        {
            entity.Width = outputWidth;
            entity.Height = outputHeight;
        }

        // Same reasoning for the seed: a request carrying the random sentinel -1 must record the seed the runtime
        // actually drew, or the image can never be reproduced. Guarded so a runtime that reported no seed (negative)
        // leaves the requested value alone rather than overwriting it with another sentinel.
        if (resolvedSeed >= 0)
        {
            entity.Seed = resolvedSeed;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid jobId, string sanitizedError, long completedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedError);

        var entity = await LoadTrackedAsync(jobId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Status = ImageJobStatus.Failed;
        entity.SanitizedError = sanitizedError;
        entity.CompletedAtUtc = completedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkCancelledAsync(Guid jobId, long completedAtUtc, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(jobId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.Status = ImageJobStatus.Cancelled;
        entity.CompletedAtUtc = completedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkCancellationRequestedAsync(Guid jobId, long requestedAtUtc, CancellationToken cancellationToken)
    {
        var entity = await LoadTrackedAsync(jobId, cancellationToken);
        if (entity is null)
        {
            return;
        }

        entity.CancellationRequestedAtUtc = requestedAtUtc;
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> MarkInterruptedFailedAsync(string sanitizedError, long completedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sanitizedError);

        // Tracked load (like the single-job Mark* methods) so the status-only mutation leaves the prompt property
        // unmodified and the stored ciphertext is preserved.
        var entities = await _dbContext.ImageJobs
                                       .Where(job => job.Status == ImageJobStatus.Queued || job.Status == ImageJobStatus.Generating)
                                       .ToListAsync(cancellationToken);
        if (entities.Count == 0)
        {
            return [];
        }

        foreach (var entity in entities)
        {
            entity.Status = ImageJobStatus.Failed;
            entity.SanitizedError = sanitizedError;
            entity.CompletedAtUtc = completedAtUtc;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return entities.Select(entity => entity.Id).ToArray();
    }

    // Loads a tracked entity so a status-only mutation leaves the prompt property unmodified (the SaveChanges interceptor
    // skips re-encrypting an unmodified required column, preserving the stored ciphertext).
    private Task<ImageJob?> LoadTrackedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return _dbContext.ImageJobs.FirstOrDefaultAsync(job => job.Id == jobId, cancellationToken);
    }

    private static ImageJobView ToView(ImageJob entity)
    {
        return new ImageJobView
        {
            Id = entity.Id,
            ModelName = entity.ModelName,
            Prompt = Encoding.UTF8.GetString(entity.Prompt),
            NegativePrompt = entity.NegativePrompt is null ? null : Encoding.UTF8.GetString(entity.NegativePrompt),
            Seed = entity.Seed,
            Width = entity.Width,
            Height = entity.Height,
            Steps = entity.Steps,
            Sampler = entity.Sampler,
            CfgScale = entity.CfgScale,
            Status = entity.Status,
            CreatedAtUtc = entity.CreatedAtUtc,
            StartedAtUtc = entity.StartedAtUtc,
            CompletedAtUtc = entity.CompletedAtUtc,
            DurationMs = entity.DurationMs,
            ImageId = entity.ImageId,
            SanitizedError = entity.SanitizedError,
            CancellationRequestedAtUtc = entity.CancellationRequestedAtUtc
        };
    }
}
