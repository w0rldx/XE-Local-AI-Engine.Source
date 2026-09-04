namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>EF persistence boundary for integration triggers. Every column here is plaintext structural.</summary>
public sealed class IntegrationTriggerStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IIntegrationTriggerStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
            SessionPolicy = command.SessionPolicy,
            AcceptedInputKinds = command.AcceptedInputKinds,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        };

        _ = _dbContext.IntegrationTriggers.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(entity);
    }

    public async Task<bool> UpdateAsync(IntegrationTriggerUpdateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = await _dbContext.IntegrationTriggers.SingleOrDefaultAsync(row => row.Id == command.TriggerId, cancellationToken).ConfigureAwait(false);
        if (entity is null || entity.Version != command.ExpectedVersion)
        {
            return false;
        }

        entity.DisplayName = command.DisplayName;
        entity.Description = command.Description;
        entity.Enabled = command.Enabled;
        entity.TargetAgentDefinitionId = command.TargetAgentDefinitionId;
        entity.SessionPolicy = command.SessionPolicy;
        entity.AcceptedInputKinds = command.AcceptedInputKinds;
        entity.UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        entity.Version++;

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        var entity = await _dbContext.IntegrationTriggers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == triggerId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IntegrationTriggerSnapshot?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationTriggers.AsNoTracking().SingleOrDefaultAsync(row => row.Name == name, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.IntegrationTriggers.AsNoTracking()
                                       .OrderBy(row => row.Name)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return [.. entities.Select(ToSnapshot)];
    }

    public async Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default)
    {
        var deleted = await _dbContext.IntegrationTriggers.Where(row => row.Id == triggerId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return deleted > 0;
    }

    private static IntegrationTriggerSnapshot ToSnapshot(IntegrationTrigger entity) =>
        new(entity.Id,
            entity.Name,
            entity.DisplayName,
            entity.Description,
            entity.Enabled,
            entity.TargetKind,
            entity.TargetAgentDefinitionId,
            entity.SessionPolicy,
            entity.AcceptedInputKinds,
            entity.CreatedAtUtc,
            entity.UpdatedAtUtc,
            entity.Version);
}
