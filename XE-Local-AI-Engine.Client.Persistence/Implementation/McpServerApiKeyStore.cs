namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the singleton inbound-MCP bearer credential.
/// </summary>
public sealed class McpServerApiKeyStore : IMcpServerApiKeyStore
{
    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public McpServerApiKeyStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<McpServerApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServerApiKeys
                                     .FirstOrDefaultAsync(row => row.Id == McpServerApiKey.SingletonId, cancellationToken);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<McpServerApiKeyRecord> SetAsync(string prefix,
        ReadOnlyMemory<byte> keyHash,
        int scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        if (keyHash.IsEmpty)
        {
            throw new ArgumentException("The key hash must not be empty — an empty digest would authenticate nothing.", nameof(keyHash));
        }

        if (scope is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "MCP API key scope must be delegate (0) or agentic (1).");
        }

        // Copy rather than alias: the entity owns its bytes for the lifetime of the tracked graph, and the caller must
        // not be able to mutate a persisted credential out from under the encryption interceptor.
        var storedHash = keyHash.ToArray();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var generationId = Guid.NewGuid();
        var entity = await _dbContext.McpServerApiKeys
                                     .FirstOrDefaultAsync(row => row.Id == McpServerApiKey.SingletonId, cancellationToken);

        if (entity is null)
        {
            entity = new McpServerApiKey
            {
                Id = McpServerApiKey.SingletonId,
                Prefix = prefix,
                KeyHash = storedHash,
                Scope = scope,
                GenerationId = generationId,
                CreatedAtUtc = now,
                LastUsedAtUtc = null
            };

            _ = _dbContext.McpServerApiKeys.Add(entity);
        }
        else
        {
            entity.Prefix = prefix;
            entity.KeyHash = storedHash;
            entity.Scope = scope;
            entity.GenerationId = generationId;
            // A regenerated key is a NEW credential: reset both stamps so the settings UI cannot show a last-used time
            // that belonged to the key this one just replaced.
            entity.CreatedAtUtc = now;
            entity.LastUsedAtUtc = null;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServerApiKeys
                                     .FirstOrDefaultAsync(row => row.Id == McpServerApiKey.SingletonId, cancellationToken);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.McpServerApiKeys.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TouchLastUsedAsync(Guid generationId,
        long timestampUtc,
        CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate rather than a tracked save: it touches ONLY last_used_at_utc, so the sealed hash column is never re-read, re-encrypted or rewritten on the
        // authentication hot path. A tracked save would round-trip the credential through the interceptors on every single authenticated MCP request.
        var updated = await _dbContext.McpServerApiKeys
                                      .Where(row => row.Id == McpServerApiKey.SingletonId && row.GenerationId == generationId)
                                      .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastUsedAtUtc, timestampUtc), cancellationToken);
        return updated == 1;
    }

    private static McpServerApiKeyRecord ToRecord(McpServerApiKey entity)
    {
        return new McpServerApiKeyRecord
        {
            Prefix = entity.Prefix,
            KeyHash = entity.KeyHash,
            Scope = entity.Scope,
            GenerationId = entity.GenerationId,
            CreatedAtUtc = entity.CreatedAtUtc,
            LastUsedAtUtc = entity.LastUsedAtUtc
        };
    }
}
