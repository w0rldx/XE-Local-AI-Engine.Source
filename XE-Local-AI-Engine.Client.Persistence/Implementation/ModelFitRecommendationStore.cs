namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for normalized model-fit recommendation rows. No column is encrypted. The per-snapshot
///     replace deletes the snapshot's existing rows and inserts the new set in one transaction.
/// </summary>
public sealed class ModelFitRecommendationStore(NodeChatDbContext dbContext) : IModelFitRecommendationStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitRecommendationInput> recommendations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recommendations);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ = await _dbContext.ModelFitRecommendations
                            .Where(recommendation => recommendation.SnapshotId == snapshotId)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        var entities = recommendations
                       .Select(input => new ModelFitRecommendation
                       {
                           Id = Guid.NewGuid(),
                           SnapshotId = snapshotId,
                           Rank = input.Rank,
                           ModelName = input.ModelName,
                           ProviderModelName = input.ProviderModelName,
                           Score = input.Score,
                           FitLevel = input.FitLevel,
                           RunMode = input.RunMode,
                           Quantization = input.Quantization,
                           EstimatedTokensPerSecond = input.EstimatedTokensPerSecond,
                           RequiredRamMb = input.RequiredRamMb,
                           RequiredVramMb = input.RequiredVramMb,
                           ContextTokens = input.ContextTokens,
                           IsInstalled = input.IsInstalled,
                           PullModelName = input.PullModelName,
                           DiagnosticsJson = input.DiagnosticsJson
                       })
                       .ToArray();

        if (entities.Length > 0)
        {
            await _dbContext.ModelFitRecommendations.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return entities.Length;
    }

    public async Task<IReadOnlyList<ModelFitRecommendationRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ModelFitRecommendations
                                       .AsNoTracking()
                                       .Where(recommendation => recommendation.SnapshotId == snapshotId)
                                       .OrderBy(recommendation => recommendation.Rank)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static ModelFitRecommendationRecord ToRecord(ModelFitRecommendation entity)
    {
        return new ModelFitRecommendationRecord(entity.Id,
            entity.SnapshotId,
            entity.Rank,
            entity.ModelName,
            entity.ProviderModelName,
            entity.Score,
            entity.FitLevel,
            entity.RunMode,
            entity.Quantization,
            entity.EstimatedTokensPerSecond,
            entity.RequiredRamMb,
            entity.RequiredVramMb,
            entity.ContextTokens,
            entity.IsInstalled,
            entity.PullModelName,
            entity.DiagnosticsJson);
    }
}
