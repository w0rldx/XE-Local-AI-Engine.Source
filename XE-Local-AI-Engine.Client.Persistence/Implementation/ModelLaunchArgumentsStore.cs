namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the per-model extra <c>llama-server</c> argument override. The
///     <c>model_launch_arguments</c> table is keyed by model name with a <c>NOCASE</c> collation, so name lookups and the
///     upsert key are case-insensitive without any LINQ-side comparer. No column is encrypted.
/// </summary>
public sealed class ModelLaunchArgumentsStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IModelLaunchArgumentsStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<string?> GetRawArgumentsAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entity = await _dbContext.ModelLaunchArguments
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(row => row.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        return entity?.RawArguments;
    }

    public async Task<IReadOnlyList<ModelLaunchArgumentsRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ModelLaunchArguments
                                       .AsNoTracking()
                                       .OrderBy(row => row.ModelName)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<ModelLaunchArgumentsRecord> UpsertAsync(string modelName, string rawArguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(rawArguments);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var entity = await _dbContext.ModelLaunchArguments
                                     .FirstOrDefaultAsync(row => row.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new ModelLaunchArguments
            {
                ModelName = modelName,
                RawArguments = rawArguments,
                UpdatedAtUtc = now
            };

            _ = _dbContext.ModelLaunchArguments.Add(entity);
        }
        else
        {
            entity.RawArguments = rawArguments;
            entity.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entity = await _dbContext.ModelLaunchArguments
                                     .FirstOrDefaultAsync(row => row.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.ModelLaunchArguments.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ModelLaunchArgumentsRecord ToRecord(ModelLaunchArguments entity)
    {
        return new ModelLaunchArgumentsRecord(entity.ModelName, entity.RawArguments, entity.UpdatedAtUtc);
    }
}
