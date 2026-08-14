namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the per-model→provider map. The <c>model_provider_map</c> table is keyed by model
///     name with a <c>NOCASE</c> collation, so name lookups and the upsert key are case-insensitive without any
///     LINQ-side comparer. No column is encrypted.
/// </summary>
internal sealed class ModelProviderMapStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IModelProviderMapStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<string?> GetProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        return (await ReadAsync(modelName, cancellationToken).ConfigureAwait(false))?.ProviderName;
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
        var revision = CreateRevision();

        var entity = await _dbContext.ModelProviderMaps
                                     .FirstOrDefaultAsync(mapping => mapping.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new ModelProviderMap
            {
                ModelName = modelName,
                ProviderName = providerName,
                UpdatedAtUtc = now,
                Revision = revision
            };

            _ = _dbContext.ModelProviderMaps.Add(entity);
        }
        else
        {
            entity.ProviderName = providerName;
            entity.UpdatedAtUtc = now;
            entity.Revision = revision;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<ModelProviderMapRecord?> ReadAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entity = await _dbContext.ModelProviderMaps
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(mapping => mapping.ModelName == modelName, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<ModelProviderMapRecord?> TryInsertAsync(string modelName,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        DetachTracked(modelName);
        var entity = new ModelProviderMap
        {
            ModelName = modelName,
            ProviderName = providerName,
            UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Revision = CreateRevision()
        };

        _ = _dbContext.ModelProviderMaps.Add(entity);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(entity);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 })
        {
            _dbContext.Entry(entity).State = EntityState.Detached;
            return null;
        }
    }

    public async Task<ModelProviderMapRecord?> TryUpdateAsync(string modelName,
        string providerName,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var revision = CreateRevision();
        var affected = await _dbContext.ModelProviderMaps
                                       .Where(mapping => mapping.ModelName == modelName && mapping.Revision == expectedRevision)
                                       .ExecuteUpdateAsync(setters => setters
                                                                      .SetProperty(mapping => mapping.ProviderName, providerName)
                                                                      .SetProperty(mapping => mapping.UpdatedAtUtc, updatedAtUtc)
                                                                      .SetProperty(mapping => mapping.Revision, revision),
                                           cancellationToken)
                                       .ConfigureAwait(false);

        DetachTracked(modelName);

        return affected == 1 ? new ModelProviderMapRecord(modelName, providerName, updatedAtUtc, revision) : null;
    }

    public async Task<bool> TryDeleteAsync(string modelName,
        string expectedProviderName,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);

        var affected = await _dbContext.ModelProviderMaps
                                       .Where(mapping => mapping.ModelName == modelName
                                                         && mapping.ProviderName == expectedProviderName
                                                         && mapping.Revision == expectedRevision)
                                       .ExecuteDeleteAsync(cancellationToken)
                                       .ConfigureAwait(false);
        DetachTracked(modelName);
        return affected == 1;
    }

    private static ModelProviderMapRecord ToRecord(ModelProviderMap entity)
    {
        return new ModelProviderMapRecord(entity.ModelName, entity.ProviderName, entity.UpdatedAtUtc, entity.Revision);
    }

    private void DetachTracked(string modelName)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries<ModelProviderMap>()
                                        .Where(entry => string.Equals(entry.Entity.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                                        .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static string CreateRevision() =>
        Guid.NewGuid().ToString("N");
}
