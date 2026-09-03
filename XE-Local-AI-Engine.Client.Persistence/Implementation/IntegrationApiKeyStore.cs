namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     EF persistence boundary for the <c>xeint_</c> credentials. The digest column is sealed by the save interceptor
///     and decrypted by the materialization interceptor, so every read here must materialize an entity rather than
///     project the column.
/// </summary>
public sealed class IntegrationApiKeyStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IIntegrationApiKeyStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<IntegrationApiKeySnapshot> CreateAsync(IntegrationApiKeyCreateCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = new IntegrationApiKey
        {
            Id = command.KeyId,
            PrincipalId = command.PrincipalId,
            KeyPrefix = command.KeyPrefix,
            KeyHash = command.KeyHash.ToArray(),
            Label = command.Label,
            AllowedTriggerIdsJson = command.AllowedTriggerIdsJson,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.IntegrationApiKeys.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<IntegrationApiKeySnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.IntegrationApiKeys.AsNoTracking()
                                       .OrderByDescending(row => row.CreatedAtUtc)
                                       .ThenByDescending(row => row.Id)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return [.. entities.Select(ToSnapshot)];
    }

    public async Task<IntegrationApiKeySnapshot?> GetByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationApiKeys.AsNoTracking()
                                     .SingleOrDefaultAsync(row => row.KeyPrefix == keyPrefix, cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<bool> TouchLastUsedAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate rather than a tracked save: it touches ONLY last_used_at_utc, so the sealed digest is never
        // re-read, re-encrypted or rewritten on the authentication hot path.
        var updated = await _dbContext.IntegrationApiKeys.Where(row => row.Id == keyId)
                                      .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastUsedAtUtc, atUtc), cancellationToken)
                                      .ConfigureAwait(false);
        return updated > 0;
    }

    public async Task<bool> RevokeAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default)
    {
        // Soft revoke, and ExecuteUpdate for the same reason: execution and audit rows reference the prefix, so the row
        // is stamped rather than deleted, and the digest is left sealed exactly as it was written.
        var updated = await _dbContext.IntegrationApiKeys.Where(row => row.Id == keyId && row.RevokedAtUtc == null)
                                      .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.RevokedAtUtc, atUtc), cancellationToken)
                                      .ConfigureAwait(false);
        return updated > 0;
    }

    private static IntegrationApiKeySnapshot ToSnapshot(IntegrationApiKey entity) =>
        new(entity.Id,
            entity.PrincipalId,
            entity.KeyPrefix,
            entity.KeyHash,
            entity.Label,
            entity.AllowedTriggerIdsJson,
            entity.CreatedAtUtc,
            entity.LastUsedAtUtc,
            entity.RevokedAtUtc);
}
