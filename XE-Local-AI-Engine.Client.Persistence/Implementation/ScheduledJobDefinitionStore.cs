namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for scheduled job definition data.
/// </summary>
public sealed class ScheduledJobDefinitionStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IScheduledJobDefinitionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<ScheduledJobDefinitionRecord> AddAsync(ScheduledJobDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new ScheduledJobDefinition
        {
            Id = Guid.NewGuid(),
            TemplateId = input.TemplateId,
            DisplayName = input.DisplayName,
            Description = input.Description,
            Enabled = input.Enabled,
            ScheduleKind = input.ScheduleKind,
            CronExpression = input.CronExpression,
            IntervalSeconds = input.IntervalSeconds,
            RepeatCount = input.RepeatCount,
            StartAtUtc = input.StartAtUtc,
            EndAtUtc = input.EndAtUtc,
            TimeZoneId = input.TimeZoneId,
            MisfirePolicy = input.MisfirePolicy,
            PreventOverlap = input.PreventOverlap,
            MaxRuntimeSeconds = input.MaxRuntimeSeconds,
            ParameterJson = EncodeOptional(input.ParameterJson),
            CreatedBy = input.CreatedBy,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.ScheduledJobDefinitions.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<ScheduledJobDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ScheduledJobDefinitions
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ScheduledJobDefinitions.AsNoTracking();

        if (!includeDeleted)
        {
            query = query.Where(definition => definition.DeletedAtUtc == null);
        }

        var entities = await query
                             .OrderBy(definition => definition.CreatedAtUtc)
                             .ToListAsync(cancellationToken)
                             .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ScheduledJobDefinitions
                                       .AsNoTracking()
                                       .Where(definition => definition.TemplateId == templateId && definition.DeletedAtUtc == null)
                                       .OrderBy(definition => definition.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ScheduledJobDefinitions
                                       .AsNoTracking()
                                       .Where(definition => definition.Enabled && definition.DeletedAtUtc == null)
                                       .OrderBy(definition => definition.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<ScheduledJobDefinitionRecord?> UpdateAsync(Guid id, ScheduledJobDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = await _dbContext.ScheduledJobDefinitions
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        entity.TemplateId = input.TemplateId;
        entity.DisplayName = input.DisplayName;
        entity.Description = input.Description;
        entity.Enabled = input.Enabled;
        entity.ScheduleKind = input.ScheduleKind;
        entity.CronExpression = input.CronExpression;
        entity.IntervalSeconds = input.IntervalSeconds;
        entity.RepeatCount = input.RepeatCount;
        entity.StartAtUtc = input.StartAtUtc;
        entity.EndAtUtc = input.EndAtUtc;
        entity.TimeZoneId = input.TimeZoneId;
        entity.MisfirePolicy = input.MisfirePolicy;
        entity.PreventOverlap = input.PreventOverlap;
        entity.MaxRuntimeSeconds = input.MaxRuntimeSeconds;
        entity.ParameterJson = EncodeOptional(input.ParameterJson);
        entity.CreatedBy = input.CreatedBy;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<ScheduledJobDefinitionRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ScheduledJobDefinitions
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        entity.Enabled = enabled;
        entity.DisabledAtUtc = enabled ? null : now;
        entity.UpdatedAtUtc = now;

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ScheduledJobDefinitions
                                     .FirstOrDefaultAsync(definition => definition.Id == id, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        entity.DeletedAtUtc = now;
        entity.Enabled = false;
        entity.UpdatedAtUtc = now;

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static ScheduledJobDefinitionRecord ToRecord(ScheduledJobDefinition entity)
    {
        return new ScheduledJobDefinitionRecord(entity.Id,
            entity.TemplateId,
            entity.DisplayName,
            entity.Description,
            entity.Enabled,
            entity.ScheduleKind,
            entity.CronExpression,
            entity.IntervalSeconds,
            entity.RepeatCount,
            entity.StartAtUtc,
            entity.EndAtUtc,
            entity.TimeZoneId,
            entity.MisfirePolicy,
            entity.PreventOverlap,
            entity.MaxRuntimeSeconds,
            entity.ParameterJson is null ? null : Decode(entity.ParameterJson),
            entity.CreatedBy,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.DisabledAtUtc,
            entity.DeletedAtUtc);
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
