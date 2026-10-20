namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<IReadOnlyList<BenchmarkRunArtifactRecord>> AddRunArtifactsAsync(Guid runId,
        Guid judgeAttemptId,
        IReadOnlyList<BenchmarkRunArtifactCommand> artifacts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Any(static artifact => artifact.Id == Guid.Empty
                                             || string.IsNullOrWhiteSpace(artifact.CriterionId)
                                             || !BenchmarkRunArtifactKinds.IsKnown(artifact.Kind)
                                             || string.IsNullOrWhiteSpace(artifact.MediaType)
                                             || artifact.ByteCount < 0
                                             || string.IsNullOrWhiteSpace(artifact.ContentHash)))
        {
            throw new BenchmarkValidationException("Benchmark run artifact input is invalid.");
        }

        if (!await _dbContext.BenchmarkJudgeAttempts.AnyAsync(entity => entity.Id == judgeAttemptId && entity.RunId == runId, cancellationToken))
        {
            throw new BenchmarkNotFoundException("Benchmark judge attempt was not found for this run.");
        }

        var now = Now();
        var entities = artifacts.Select(artifact => new BenchmarkRunArtifact
                                {
                                    Id = artifact.Id,
                                    RunId = runId,
                                    JudgeAttemptId = judgeAttemptId,
                                    CriterionId = artifact.CriterionId!,
                                    Kind = artifact.Kind,
                                    MediaType = artifact.MediaType,
                                    ByteCount = artifact.ByteCount,
                                    ContentHash = artifact.ContentHash,
                                    CreatedAtUtc = now
                                })
                                .ToArray();

        // One SaveChanges is one transaction: either every stream of the judging is recorded or none is.
        _dbContext.BenchmarkRunArtifacts.AddRange(entities);
        try
        {
            await SaveAsync(cancellationToken);
        }
        catch (BenchmarkConflictException)
        {
            // A refused insert must not stay tracked as Added: the next save on this context would retry it and fail again.
            foreach (var entity in entities)
            {
                _dbContext.Entry(entity).State = EntityState.Detached;
            }

            throw;
        }

        return [.. entities.Select(ToRecord)];
    }

    public async Task<IReadOnlyList<BenchmarkRunArtifactRecord>> GetRunArtifactsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.BenchmarkRunArtifacts.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .OrderBy(static entity => entity.CreatedAtUtc)
                                       .ThenBy(static entity => entity.CriterionId)
                                       .ThenBy(static entity => entity.Kind)
                                       .ToArrayAsync(cancellationToken);
        return [.. entities.Select(ToRecord)];
    }

    private static BenchmarkRunArtifactRecord ToRecord(BenchmarkRunArtifact entity) =>
        new()
        {
            Id = entity.Id,
            RunId = entity.RunId,
            JudgeAttemptId = entity.JudgeAttemptId,
            CriterionId = entity.CriterionId,
            Kind = entity.Kind,
            MediaType = entity.MediaType,
            ByteCount = entity.ByteCount,
            ContentHash = entity.ContentHash,
            CreatedAtUtc = entity.CreatedAtUtc
        };
}
