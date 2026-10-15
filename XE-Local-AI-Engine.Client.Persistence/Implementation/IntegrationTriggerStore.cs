namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>EF persistence boundary for integration triggers. Every column here is plaintext structural.</summary>
public sealed class IntegrationTriggerStore : IIntegrationTriggerStore
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public IntegrationTriggerStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IntegrationTriggerSnapshot> CreateAsync(IntegrationTriggerCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = new IntegrationTrigger
        {
            Id = command.TriggerId,
            Name = command.Name,
            DisplayName = command.DisplayName,
            Description = command.Description,
            Enabled = command.Enabled,
            TargetKind = command.TargetKind,
            TargetAgentDefinitionId = command.TargetAgentDefinitionId,
            TargetGraphWorkflowDefinitionId = command.TargetGraphWorkflowDefinitionId,
            SessionPolicy = command.SessionPolicy,
            AcceptedInputKinds = command.AcceptedInputKinds,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        };

        _ = _dbContext.IntegrationTriggers.Add(entity);
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (!IsCheckViolation(exception))
        {
            // The service's name pre-check is not atomic with this insert; the unique index on the name decides the race.
            throw new IntegrationTriggerNameConflictException($"Another trigger already uses the name '{command.Name}'.", exception);
        }

        return ToSnapshot(entity);
    }

    public async Task<bool> UpdateAsync(IntegrationTriggerUpdateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await _dbContext.IntegrationTriggers.SingleOrDefaultAsync(row => row.Id == command.TriggerId, cancellationToken);
        if (entity is null || entity.Version != command.ExpectedVersion)
        {
            return false;
        }

        entity.DisplayName = command.DisplayName;
        entity.Description = command.Description;
        entity.Enabled = command.Enabled;
        entity.TargetKind = command.TargetKind;
        entity.TargetAgentDefinitionId = command.TargetAgentDefinitionId;
        entity.TargetGraphWorkflowDefinitionId = command.TargetGraphWorkflowDefinitionId;
        entity.SessionPolicy = command.SessionPolicy;
        entity.AcceptedInputKinds = command.AcceptedInputKinds;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        entity.Version++;

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The query-first check alone is not atomic; the concurrency token is what makes two racing updates resolve
            // to exactly one winner, and the loser must learn it lost without a try/catch of its own.
            return false;
        }

        return true;
    }

    public async Task<IntegrationTriggerSnapshot?> GetByIdAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationTriggers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == triggerId, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IntegrationTriggerSnapshot?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationTriggers.AsNoTracking().SingleOrDefaultAsync(row => row.Name == name, cancellationToken);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.IntegrationTriggers.AsNoTracking()
                                       .OrderBy(row => row.Name)
                                       .ToListAsync(cancellationToken);
        return [.. entities.Select(ToSnapshot)];
    }

    public async Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        var deleted = await _dbContext.IntegrationTriggers.Where(row => row.Id == triggerId).ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    /// <summary>The one-target CHECK, which is a caller bug rather than a lost name race, so it is not reported as one.</summary>
    private static bool IsCheckViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 275 };

    private static IntegrationTriggerSnapshot ToSnapshot(IntegrationTrigger entity) =>
        new()
        {
            Id = entity.Id,
            Name = entity.Name,
            DisplayName = entity.DisplayName,
            Description = entity.Description,
            Enabled = entity.Enabled,
            TargetKind = entity.TargetKind,
            TargetAgentDefinitionId = entity.TargetAgentDefinitionId,
            TargetGraphWorkflowDefinitionId = entity.TargetGraphWorkflowDefinitionId,
            SessionPolicy = entity.SessionPolicy,
            AcceptedInputKinds = entity.AcceptedInputKinds,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Version = entity.Version
        };
}
