namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary><see cref="IModelLabPlaygroundRunStore" /> over <see cref="NodeChatDbContext" />. A row is written once and never updated.</summary>
public sealed class ModelLabPlaygroundRunStore : IModelLabPlaygroundRunStore
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ModelLabPlaygroundRunStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<ModelLabPlaygroundRunRecord> CreateAsync(ModelLabPlaygroundRunCreate command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var row = new ModelLabPlaygroundRun
        {
            Id = command.Id,
            TemplateId = command.TemplateId,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            TraceBlobId = command.TraceBlobId,
            TraceSha256 = command.TraceSha256,
            TraceByteCount = command.TraceByteCount,
            ResultJson = command.ResultJson.ToArray()
        };
        _ = _dbContext.ModelLabPlaygroundRuns.Add(row);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(row);
    }

    public async Task<ModelLabPlaygroundRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.ModelLabPlaygroundRuns.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<ModelLabPlaygroundRunRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Entities, not a projection: only the materialization interceptor decrypts the result column.
        var rows = await _dbContext.ModelLabPlaygroundRuns.AsNoTracking()
                                   .OrderByDescending(item => item.CreatedAtUtc)
                                   .ThenByDescending(item => item.Id)
                                   .ToListAsync(cancellationToken);
        return [.. rows.Select(ToRecord)];
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        await _dbContext.ModelLabPlaygroundRuns.Where(item => item.Id == id).ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<IReadOnlyList<Guid>> TrimToNewestAsync(int keep, CancellationToken cancellationToken = default)
    {
        var candidates = await _dbContext.ModelLabPlaygroundRuns
                                         .OrderByDescending(item => item.CreatedAtUtc)
                                         .ThenByDescending(item => item.Id)
                                         .Skip(keep)
                                         .Select(item => item.Id)
                                         .ToListAsync(cancellationToken);
        // One transaction: a delete that fails rolls back the ones before it, whose ids would never reach the blob cleanup.
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var removed = new List<Guid>();
        foreach (var id in candidates)
        {
            // Only a delete this call saw land is reported, so the caller never drops the blobs of a row it did not remove.
            if (await DeleteAsync(id, cancellationToken))
            {
                removed.Add(id);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static ModelLabPlaygroundRunRecord ToRecord(ModelLabPlaygroundRun row) =>
        new()
        {
            Id = row.Id,
            TemplateId = row.TemplateId,
            CreatedAtUtc = row.CreatedAtUtc,
            TraceBlobId = row.TraceBlobId,
            TraceSha256 = row.TraceSha256,
            TraceByteCount = row.TraceByteCount,
            ResultJson = row.ResultJson
        };
}
