namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for normalized model-fit recommendation rows. No column is encrypted. The per-snapshot
///     replace deletes the snapshot's existing rows and inserts the new set in one transaction.
/// </summary>
public sealed class ModelFitRecommendationStore : IModelFitRecommendationStore
{
    private readonly NodeChatDbContext _dbContext;

    public ModelFitRecommendationStore(NodeChatDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitRecommendationInput> recommendations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recommendations);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        _ = await _dbContext.ModelFitRecommendations
                            .Where(recommendation => recommendation.SnapshotId == snapshotId)
                            .ExecuteDeleteAsync(cancellationToken);

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
            await _dbContext.ModelFitRecommendations.AddRangeAsync(entities, cancellationToken);
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return entities.Length;
    }

    public async Task<IReadOnlyList<ModelFitRecommendationRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ModelFitRecommendations
                                       .AsNoTracking()
                                       .Where(recommendation => recommendation.SnapshotId == snapshotId)
                                       .OrderBy(recommendation => recommendation.Rank)
                                       .ToListAsync(cancellationToken);

        return entities.Select(ToRecord).ToArray();
    }

    private static ModelFitRecommendationRecord ToRecord(ModelFitRecommendation entity)
    {
        return new ModelFitRecommendationRecord
        {
            Id = entity.Id,
            SnapshotId = entity.SnapshotId,
            Rank = entity.Rank,
            ModelName = entity.ModelName,
            ProviderModelName = entity.ProviderModelName,
            Score = entity.Score,
            FitLevel = entity.FitLevel,
            RunMode = entity.RunMode,
            Quantization = entity.Quantization,
            EstimatedTokensPerSecond = entity.EstimatedTokensPerSecond,
            RequiredRamMb = entity.RequiredRamMb,
            RequiredVramMb = entity.RequiredVramMb,
            ContextTokens = entity.ContextTokens,
            IsInstalled = entity.IsInstalled,
            PullModelName = entity.PullModelName,
            DiagnosticsJson = entity.DiagnosticsJson
        };
    }
}
