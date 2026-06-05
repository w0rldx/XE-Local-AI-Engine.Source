namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for scheduled job run event data.
/// </summary>
public sealed class ScheduledJobRunEventStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IScheduledJobRunEventStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ScheduledJobRunEventRecord> AddAsync(ScheduledJobRunEventInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = new ScheduledJobRunEvent
        {
            Id = Guid.NewGuid(),
            RunId = input.RunId,
            Sequence = input.Sequence,
            Level = input.Level,
            Message = input.Message,
            DataJson = EncodeOptional(input.DataJson),
            OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.ScheduledJobRunEvents.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<IReadOnlyList<ScheduledJobRunEventRecord>> ListByRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ScheduledJobRunEvents
                                       .AsNoTracking()
                                       .Where(runEvent => runEvent.RunId == runId)
                                       .OrderBy(runEvent => runEvent.Sequence)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    private static ScheduledJobRunEventRecord ToRecord(ScheduledJobRunEvent entity)
    {
        return new ScheduledJobRunEventRecord(entity.Id,
            entity.RunId,
            entity.Sequence,
            entity.Level,
            entity.Message,
            entity.DataJson is null ? null : Decode(entity.DataJson),
            entity.OccurredAtUtc);
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
