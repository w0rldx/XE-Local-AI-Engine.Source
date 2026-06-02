namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for measured model-fit benchmark rows. The raw output and diagnostics columns are encrypted
///     at rest by the node encryption interceptors; this store passes them as plaintext strings and returns them
///     decrypted on the record. The per-snapshot replace deletes the snapshot's existing rows and inserts the new set in
///     one transaction.
/// </summary>
public sealed class ModelFitBenchmarkStore(NodeChatDbContext dbContext) : IModelFitBenchmarkStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitBenchmarkInput> benchmarks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(benchmarks);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ = await _dbContext.ModelFitBenchmarks
                            .Where(benchmark => benchmark.SnapshotId == snapshotId)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        var entities = benchmarks
                       .Select(input => new ModelFitBenchmark
                       {
                           Id = Guid.NewGuid(),
                           SnapshotId = snapshotId,
                           ModelName = input.ModelName,
                           ProviderName = input.ProviderName,
                           TokensPerSecond = input.TokensPerSecond,
                           TtftMs = input.TtftMs,
                           TotalLatencyMs = input.TotalLatencyMs,
                           Runs = input.Runs,
                           RawJson = EncodeOptional(input.RawJson),
                           DiagnosticsJson = EncodeOptional(input.DiagnosticsJson)
                       })
                       .ToArray();

        if (entities.Length > 0)
        {
            await _dbContext.ModelFitBenchmarks.AddRangeAsync(entities, cancellationToken).ConfigureAwait(false);
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return entities.Length;
    }

    public async Task<IReadOnlyList<ModelFitBenchmarkRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ModelFitBenchmarks
                                       .AsNoTracking()
                                       .Where(benchmark => benchmark.SnapshotId == snapshotId)
                                       .OrderBy(benchmark => benchmark.ModelName)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static ModelFitBenchmarkRecord ToRecord(ModelFitBenchmark entity)
    {
        return new ModelFitBenchmarkRecord(
            entity.Id,
            entity.SnapshotId,
            entity.ModelName,
            entity.ProviderName,
            entity.TokensPerSecond,
            entity.TtftMs,
            entity.TotalLatencyMs,
            entity.Runs,
            entity.RawJson is null ? null : Decode(entity.RawJson),
            entity.DiagnosticsJson is null ? null : Decode(entity.DiagnosticsJson));
    }

    private static byte[]? EncodeOptional(string? value)
    {
        return value is null ? null : Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }
}
