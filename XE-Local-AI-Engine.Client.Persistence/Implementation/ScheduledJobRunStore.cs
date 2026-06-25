namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for scheduled job run data.
/// </summary>
public sealed class ScheduledJobRunStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IScheduledJobRunStore
{
    private static readonly ScheduledRunStatus[] ActiveStatuses = [ScheduledRunStatus.Queued, ScheduledRunStatus.Running];

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ScheduledJobRunRecord> AddAsync(ScheduledJobRunInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = BuildEntity(input);

        _ = _dbContext.ScheduledJobRuns.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<ScheduledJobRunRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ScheduledJobRuns
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(run => run.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ScheduledJobRunRecord>> ListByJobAsync(Guid scheduledJobId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ScheduledJobRuns
                                       .AsNoTracking()
                                       .Where(run => run.ScheduledJobId == scheduledJobId)
                                       .OrderByDescending(run => run.ActualFireTimeUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<ScheduledJobRunRecord>> ListAsync(ScheduledRunStatus? status = null,
        long? fromUtc = null,
        long? toUtc = null,
        Guid? scheduledJobId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ScheduledJobRuns.AsNoTracking();

        if (status is not null)
        {
            query = query.Where(run => run.Status == status.Value);
        }

        if (scheduledJobId is not null)
        {
            query = query.Where(run => run.ScheduledJobId == scheduledJobId.Value);
        }

        if (fromUtc is not null)
        {
            query = query.Where(run => run.ActualFireTimeUtc >= fromUtc.Value);
        }

        if (toUtc is not null)
        {
            query = query.Where(run => run.ActualFireTimeUtc <= toUtc.Value);
        }

        var entities = await query
                             .OrderByDescending(run => run.ActualFireTimeUtc)
                             .ToListAsync(cancellationToken)
                             .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<ScheduledJobRunRecord> UpsertByFireInstanceAsync(ScheduledJobRunInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.IsNullOrEmpty(input.QuartzFireInstanceId))
        {
            var existing = await _dbContext.ScheduledJobRuns
                                           .FirstOrDefaultAsync(run => run.QuartzFireInstanceId == input.QuartzFireInstanceId, cancellationToken)
                                           .ConfigureAwait(false);

            if (existing is not null)
            {
                return ToRecord(existing);
            }
        }

        var entity = BuildEntity(input);

        _ = _dbContext.ScheduledJobRuns.Add(entity);

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (!string.IsNullOrEmpty(input.QuartzFireInstanceId))
        {
            // A concurrent caller won the race on the same fire-instance id (the filtered unique index rejected this
            // insert). Detach our losing entity and return the row the winner committed — the upsert stays idempotent.
            _dbContext.Entry(entity).State = EntityState.Detached;

            var winner = await _dbContext.ScheduledJobRuns
                                         .AsNoTracking()
                                         .FirstOrDefaultAsync(run => run.QuartzFireInstanceId == input.QuartzFireInstanceId, cancellationToken)
                                         .ConfigureAwait(false);

            if (winner is not null)
            {
                return ToRecord(winner);
            }

            throw;
        }

        return ToRecord(entity);
    }

    public async Task<ScheduledJobRunRecord?> UpdateLifecycleAsync(Guid id,
        ScheduledRunStatus status,
        long? completedAtUtc = null,
        long? durationMs = null,
        string? summary = null,
        string? detailsJson = null,
        string? errorMessage = null,
        string? errorDetails = null,
        CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ScheduledJobRuns
                                     .FirstOrDefaultAsync(run => run.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.Status = status;

        if (completedAtUtc is not null)
        {
            entity.CompletedAtUtc = completedAtUtc;
        }

        if (durationMs is not null)
        {
            entity.DurationMs = durationMs;
        }

        if (summary is not null)
        {
            entity.Summary = summary;
        }

        if (detailsJson is not null)
        {
            entity.DetailsJson = Encoding.UTF8.GetBytes(detailsJson);
        }

        if (errorMessage is not null)
        {
            entity.ErrorMessage = errorMessage;
        }

        if (errorDetails is not null)
        {
            entity.ErrorDetails = errorDetails;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<ScheduledJobRunRecord?> RequestCancellationAsync(Guid id, long requestedAtUtc, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ScheduledJobRuns
                                     .FirstOrDefaultAsync(run => run.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.CancellationRequestedAtUtc = requestedAtUtc;

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<int> MarkStaleActiveRunsAsync(ScheduledRunStatus terminalStatus, string reason, CancellationToken cancellationToken = default)
    {
        var staleRuns = await _dbContext.ScheduledJobRuns
                                        .Where(run => ActiveStatuses.Contains(run.Status))
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);

        if (staleRuns.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        foreach (var run in staleRuns)
        {
            run.Status = terminalStatus;
            run.CompletedAtUtc = now;
            run.ErrorMessage = reason;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return staleRuns.Count;
    }

    public async Task<int> SweepOlderThanAsync(long cutoffUtc, CancellationToken cancellationToken = default)
    {
        var expiredRunIds = await _dbContext.ScheduledJobRuns
                                            .Where(run => run.CreatedAtUtc < cutoffUtc)
                                            .Select(run => run.Id)
                                            .ToListAsync(cancellationToken)
                                            .ConfigureAwait(false);

        if (expiredRunIds.Count == 0)
        {
            return 0;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ = await _dbContext.ScheduledJobRunEvents
                            .Where(runEvent => expiredRunIds.Contains(runEvent.RunId))
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        var deletedRunCount = await _dbContext.ScheduledJobRuns
                                              .Where(run => expiredRunIds.Contains(run.Id))
                                              .ExecuteDeleteAsync(cancellationToken)
                                              .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return deletedRunCount;
    }

    private ScheduledJobRun BuildEntity(ScheduledJobRunInput input)
    {
        return new ScheduledJobRun
        {
            Id = Guid.NewGuid(),
            ScheduledJobId = input.ScheduledJobId,
            TemplateId = input.TemplateId,
            QuartzFireInstanceId = input.QuartzFireInstanceId,
            TriggeredBy = input.TriggeredBy,
            Status = input.Status,
            ScheduledFireTimeUtc = input.ScheduledFireTimeUtc,
            ActualFireTimeUtc = input.ActualFireTimeUtc,
            Summary = input.Summary,
            DetailsJson = EncodeOptional(input.DetailsJson),
            ErrorMessage = input.ErrorMessage,
            ErrorDetails = input.ErrorDetails,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
    }

    private static ScheduledJobRunRecord ToRecord(ScheduledJobRun entity)
    {
        return new ScheduledJobRunRecord(entity.Id,
            entity.ScheduledJobId,
            entity.TemplateId,
            entity.QuartzFireInstanceId,
            entity.TriggeredBy,
            entity.Status,
            entity.ScheduledFireTimeUtc,
            entity.ActualFireTimeUtc,
            entity.CompletedAtUtc,
            entity.DurationMs,
            entity.Summary,
            entity.DetailsJson is null ? null : Decode(entity.DetailsJson),
            entity.ErrorMessage,
            entity.ErrorDetails,
            entity.CancellationRequestedAtUtc,
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
