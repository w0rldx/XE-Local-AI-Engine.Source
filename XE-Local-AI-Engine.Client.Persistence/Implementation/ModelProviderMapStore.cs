namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for the per-model→provider map. The <c>model_provider_map</c> table is keyed by model
///     name with a <c>NOCASE</c> collation, so name lookups and the upsert key are case-insensitive without any
///     LINQ-side comparer. No column is encrypted.
/// </summary>
public sealed class ModelProviderMapStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IModelProviderMapStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<string?> GetProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entity = await _dbContext.ModelProviderMaps
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(mapping => mapping.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        return entity?.ProviderName;
    }

    public async Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ModelProviderMaps
                                       .AsNoTracking()
                                       .OrderBy(mapping => mapping.ModelName)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<ModelProviderMapRecord> UpsertAsync(string modelName, string providerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var entity = await _dbContext.ModelProviderMaps
                                     .FirstOrDefaultAsync(mapping => mapping.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new ModelProviderMap
            {
                ModelName = modelName,
                ProviderName = providerName,
                UpdatedAtUtc = now
            };

            _ = _dbContext.ModelProviderMaps.Add(entity);
        }
        else
        {
            entity.ProviderName = providerName;
            entity.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    private static ModelProviderMapRecord ToRecord(ModelProviderMap entity)
    {
        return new ModelProviderMapRecord(entity.ModelName, entity.ProviderName, entity.UpdatedAtUtc);
    }
}
