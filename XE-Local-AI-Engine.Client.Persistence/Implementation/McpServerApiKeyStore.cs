namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the singleton inbound-MCP bearer credential.
/// </summary>
public sealed class McpServerApiKeyStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IMcpServerApiKeyStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<McpServerApiKeyRecord?> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServerApiKeys
                                     .FirstOrDefaultAsync(row => row.Id == McpServerApiKey.SingletonId, cancellationToken)
                                     .ConfigureAwait(false);

        return entity is null ? null : ToRecord(entity);
    }

    public async Task<McpServerApiKeyRecord> SetAsync(string prefix, string material, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(material);

        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var entity = await _dbContext.McpServerApiKeys
                                     .FirstOrDefaultAsync(row => row.Id == McpServerApiKey.SingletonId, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            entity = new McpServerApiKey
            {
                Id = McpServerApiKey.SingletonId,
                Prefix = prefix,
                Material = Encoding.UTF8.GetBytes(material),
                CreatedAtUtc = now,
                LastUsedAtUtc = null
            };

            _ = _dbContext.McpServerApiKeys.Add(entity);
        }
        else
        {
            entity.Prefix = prefix;
            entity.Material = Encoding.UTF8.GetBytes(material);
            // A regenerated key is a NEW credential: reset both stamps so the settings UI cannot show a last-used time
            // that belonged to the key this one just replaced.
            entity.CreatedAtUtc = now;
            entity.LastUsedAtUtc = null;
        }

        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.McpServerApiKeys
                                     .FirstOrDefaultAsync(row => row.Id == McpServerApiKey.SingletonId, cancellationToken)
                                     .ConfigureAwait(false);

        if (entity is null)
        {
            return false;
        }

        _ = _dbContext.McpServerApiKeys.Remove(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task TouchLastUsedAsync(long timestampUtc, CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate rather than a tracked save: it touches ONLY last_used_at_utc, so the encrypted material column
        // is never re-read, re-encrypted or rewritten on the authentication hot path. A tracked save would round-trip
        // the secret through the interceptors on every single authenticated MCP request.
        _ = await _dbContext.McpServerApiKeys
                            .Where(row => row.Id == McpServerApiKey.SingletonId)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastUsedAtUtc, timestampUtc), cancellationToken)
                            .ConfigureAwait(false);
    }

    private static McpServerApiKeyRecord ToRecord(McpServerApiKey entity)
    {
        return new McpServerApiKeyRecord(entity.Prefix,
            Encoding.UTF8.GetString(entity.Material),
            entity.CreatedAtUtc,
            entity.LastUsedAtUtc);
    }
}
