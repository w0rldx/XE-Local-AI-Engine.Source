namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Process-lifetime memoization of the seeded "Default Assistant" id.
/// </summary>
/// <remarks>
///     A singleton, the slug being fixed for the boot, so the lookup runs at most once and a concurrent first burst
///     shares one in-flight task; the scoped <see cref="IAgentDefinitionStore" /> is resolved through a fresh scope
///     per first lookup. A <c>null</c> first result is NOT cached, so a send racing the startup seeder re-attempts
///     and picks the id up once seeding finishes.
/// </remarks>
internal sealed class DefaultAgentProvider : IDefaultAgentProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    private readonly IServiceScopeFactory _scopeFactory;

    // Guarded by _gate on write; the fast-path read is a benign race (a missed just-written value just re-enters the
    // gate, finds it set, and returns). Guid? cannot be volatile, and the gate supplies the publish barrier.
    private Guid? _cachedId;

    public DefaultAgentProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<Guid?> GetDefaultAgentIdAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedId is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedId is { } cachedInsideGate)
            {
                return cachedInsideGate;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
            var record = await store.GetBySeedSlugAsync(AgentDefaults.DefaultAgentSeedSlug, cancellationToken);

            // Cache only a present id (process-lifetime); a missing seed row stays uncached so a send that raced the
            // startup seeder re-resolves on the next send rather than pinning null for the whole process.
            if (record is not null)
            {
                _cachedId = record.Id;
            }

            return record?.Id;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
