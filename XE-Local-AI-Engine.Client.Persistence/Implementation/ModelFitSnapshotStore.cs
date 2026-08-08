namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for model-fit snapshot data. The raw output / stderr / diagnostics columns are encrypted at
///     rest by the node encryption interceptors; this store passes them as plaintext strings and never returns them on
///     the summary projection — only <see cref="GetRawByIdAsync" /> decrypts them. Marking a run succeeded transactionally
///     moves the latest-successful flag so concurrent refreshes can never leave two rows latest for one key.
/// </summary>
public sealed class ModelFitSnapshotStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IModelFitSnapshotStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ModelFitSnapshotSummaryRecord> CreateRunningAsync(ModelFitSnapshotInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = new ModelFitSnapshot
        {
            Id = Guid.NewGuid(),
            ApprovedImageId = input.ApprovedImageId,
            Operation = input.Operation,
            UseCase = input.UseCase,
            ProviderName = input.ProviderName,
            ModelName = input.ModelName,
            Status = input.Status,
            StartedAtUtc = input.StartedAtUtc,
            CompletedAtUtc = null,
            DurationMs = null,
            ExitCode = null,
            RawJson = null,
            StderrExcerpt = null,
            DiagnosticsJson = null,
            IsLatestSuccessful = false,
            CreatedByRunId = input.CreatedByRunId,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.ModelFitSnapshots.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToSummary(entity);
    }

    public async Task<ModelFitSnapshotSummaryRecord?> MarkTerminalAsync(Guid id,
        ModelFitRunStatus status,
        int? exitCode,
        long? durationMs,
        string? rawJson,
        string? stderrExcerpt,
        string? diagnosticsJson,
        long completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        // A succeeded transition must atomically demote the prior latest and promote this row, so it runs inside a
        // single transaction. Non-success transitions need no transaction (they touch one row).
        if (status != ModelFitRunStatus.Succeeded)
        {
            var entity = await _dbContext.ModelFitSnapshots
                                         .FirstOrDefaultAsync(snapshot => snapshot.Id == id, cancellationToken)
                                         .ConfigureAwait(false);

            if (entity is null)
            {
                return null;
            }

            ApplyTerminalFields(entity, status, exitCode, durationMs, rawJson, stderrExcerpt, diagnosticsJson, completedAtUtc);

            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return ToSummary(entity);
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var succeeding = await _dbContext.ModelFitSnapshots
                                         .FirstOrDefaultAsync(snapshot => snapshot.Id == id, cancellationToken)
                                         .ConfigureAwait(false);

        if (succeeding is null)
        {
            return null;
        }

        // Clear the prior latest for the SAME key (operation + use_case + provider_name + model_name, null-aware) — never
        // touching other keys — then promote this row, all in one transaction.
        var priorLatest = await _dbContext.ModelFitSnapshots
                                          .Where(snapshot =>
                                              snapshot.Id != id &&
                                              snapshot.IsLatestSuccessful &&
                                              snapshot.Operation == succeeding.Operation &&
                                              snapshot.UseCase == succeeding.UseCase &&
                                              snapshot.ProviderName == succeeding.ProviderName &&
                                              snapshot.ModelName == succeeding.ModelName)
                                          .ToListAsync(cancellationToken)
                                          .ConfigureAwait(false);

        foreach (var stale in priorLatest)
        {
            stale.IsLatestSuccessful = false;
        }

        ApplyTerminalFields(succeeding, status, exitCode, durationMs, rawJson, stderrExcerpt, diagnosticsJson, completedAtUtc);
        succeeding.IsLatestSuccessful = true;

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return ToSummary(succeeding);
    }

    public async Task<ModelFitSnapshotSummaryRecord?> GetLatestSuccessfulSummaryAsync(ModelFitOperation operation,
        string? useCase,
        string providerName,
        string? modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        var entity = await _dbContext.ModelFitSnapshots
                                     .AsNoTracking()
                                     .Where(snapshot =>
                                         snapshot.IsLatestSuccessful &&
                                         snapshot.Operation == operation &&
                                         snapshot.UseCase == useCase &&
                                         snapshot.ProviderName == providerName &&
                                         snapshot.ModelName == modelName)
                                     .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
                                     .FirstOrDefaultAsync(cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToSummary(entity);
    }

    public async Task<IReadOnlyList<ModelFitSnapshotSummaryRecord>> ListRecentSummariesAsync(ModelFitOperation? operation = null,
        string? providerName = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ModelFitSnapshots.AsNoTracking();

        if (operation is not null)
        {
            query = query.Where(snapshot => snapshot.Operation == operation.Value);
        }

        if (providerName is not null)
        {
            query = query.Where(snapshot => snapshot.ProviderName == providerName);
        }

        var entities = await query
                             .OrderByDescending(snapshot => snapshot.CreatedAtUtc)
                             .Take(limit < 1 ? 1 : limit)
                             .ToListAsync(cancellationToken)
                             .ConfigureAwait(false);

        return entities.Select(ToSummary).ToArray();
    }

    public async Task<ModelFitSnapshotRawRecord?> GetRawByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ModelFitSnapshots
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(snapshot => snapshot.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        return new ModelFitSnapshotRawRecord(entity.Id,
            entity.RawJson is null ? null : Decode(entity.RawJson),
            entity.StderrExcerpt is null ? null : Decode(entity.StderrExcerpt),
            entity.DiagnosticsJson is null ? null : Decode(entity.DiagnosticsJson));
    }

    private static void ApplyTerminalFields(ModelFitSnapshot entity,
        ModelFitRunStatus status,
        int? exitCode,
        long? durationMs,
        string? rawJson,
        string? stderrExcerpt,
        string? diagnosticsJson,
        long completedAtUtc)
    {
        entity.Status = status;
        entity.ExitCode = exitCode;
        entity.DurationMs = durationMs;
        entity.CompletedAtUtc = completedAtUtc;
        entity.RawJson = EncodeOptional(rawJson);
        entity.StderrExcerpt = EncodeOptional(stderrExcerpt);
        entity.DiagnosticsJson = EncodeOptional(diagnosticsJson);
    }

    private static ModelFitSnapshotSummaryRecord ToSummary(ModelFitSnapshot entity)
    {
        return new ModelFitSnapshotSummaryRecord(entity.Id,
            entity.ApprovedImageId,
            entity.Operation,
            entity.UseCase,
            entity.ProviderName,
            entity.ModelName,
            entity.Status,
            entity.StartedAtUtc,
            entity.CompletedAtUtc,
            entity.DurationMs,
            entity.ExitCode,
            entity.IsLatestSuccessful,
            entity.CreatedByRunId,
            entity.CreatedAtUtc);
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
