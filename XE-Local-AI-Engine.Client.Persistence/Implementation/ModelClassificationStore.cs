namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for local model classification data.
/// </summary>
/// <remarks>
///     The <c>model_classifications</c> table is keyed by model name with a <c>NOCASE</c> collation, so name lookups
///     and the upsert key are case-insensitive without any LINQ-side comparer. No column is encrypted.
/// </remarks>
public sealed class ModelClassificationStore : IModelClassificationStore
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ModelClassificationStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<ModelClassificationRecord?> GetByNameAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entity = await _dbContext.ModelClassifications
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(classification => classification.ModelName == modelName, cancellationToken);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ModelClassificationRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ModelClassifications
                                       .AsNoTracking()
                                       .OrderBy(classification => classification.ModelName)
                                       .ToListAsync(cancellationToken);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<ModelClassificationRecord> UpsertDetectedAsync(string modelName,
        string? digest,
        ModelKind detectedKind,
        string? capabilitiesJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // Load tracked so an existing operator override is preserved across the detection refresh.
        var entity = await _dbContext.ModelClassifications
                                     .FirstOrDefaultAsync(classification => classification.ModelName == modelName, cancellationToken);

        if (entity is null)
        {
            entity = new ModelClassification
            {
                ModelName = modelName,
                Digest = digest,
                DetectedKind = detectedKind,
                DetectedCapabilitiesJson = capabilitiesJson,
                OverrideKind = null,
                DetectedAtUtc = now,
                UpdatedAtUtc = now
            };

            _ = _dbContext.ModelClassifications.Add(entity);
        }
        else
        {
            entity.Digest = digest;
            entity.DetectedKind = detectedKind;
            entity.DetectedCapabilitiesJson = capabilitiesJson;
            entity.DetectedAtUtc = now;
            entity.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return ToRecord(entity);
    }

    public async Task<ModelClassificationRecord> SetOverrideAsync(string modelName,
        ModelKind? overrideKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var entity = await _dbContext.ModelClassifications
                                     .FirstOrDefaultAsync(classification => classification.ModelName == modelName, cancellationToken);

        if (entity is null)
        {
            // No row yet: persist the override against an unprobed (Unknown) detected baseline. Detection will fill the
            // detected fields later without disturbing the override.
            entity = new ModelClassification
            {
                ModelName = modelName,
                Digest = null,
                DetectedKind = ModelKind.Unknown,
                DetectedCapabilitiesJson = null,
                OverrideKind = overrideKind,
                DetectedAtUtc = null,
                UpdatedAtUtc = now
            };

            _ = _dbContext.ModelClassifications.Add(entity);
        }
        else
        {
            entity.OverrideKind = overrideKind;
            entity.UpdatedAtUtc = now;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return ToRecord(entity);
    }

    private static ModelClassificationRecord ToRecord(ModelClassification entity)
    {
        return new ModelClassificationRecord
        {
            ModelName = entity.ModelName,
            Digest = entity.Digest,
            DetectedKind = entity.DetectedKind,
            DetectedCapabilitiesJson = entity.DetectedCapabilitiesJson,
            OverrideKind = entity.OverrideKind,
            DetectedAtUtc = entity.DetectedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc
        };
    }
}
