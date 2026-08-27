namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using System.Collections.Frozen;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The <see cref="IExternalProviderRegistry" /> the node routes, gates and renders from: a cached projection of the
///     encrypted store onto the key-free read model, plus the synchronous trust lookup the policy sites on the send
///     path need.
/// </summary>
/// <remarks>
///     <para>
///         WHY a cache at all: the chat path resolves a model's connection on every cold client, every capability
///         resolution and every policy check, and each miss would otherwise be a file read plus a data-protection
///         unprotect. WHY the cache is INVALIDATED rather than time-bounded: the registry contract requires a save to
///         take effect without a restart, and a TTL would leave a window in which the node still sends to a base URL
///         the operator has already changed.
///     </para>
///     <para>
///         The snapshot is also what makes <see cref="TryClassifyCached" /> possible. Three policy sites that must
///         classify an external id — the tool offer's synchronous gate, its <c>run_python</c> gate, and
///         <c>RuntimeChatClient</c>'s egress backstop — have no async boundary to await on, and blocking a chat send on
///         a file read is not an option. They read the snapshot or fail closed, and the reconciliation pass primes it at
///         startup so the fail-closed window is the interval before the node has finished booting.
///     </para>
/// </remarks>
public sealed class ExternalProviderRegistry : IExternalProviderRegistry, IExternalProviderRegistryCache
{
    private readonly IExternalProviderStore _store;

    // Written as a whole immutable value, never mutated in place, so a reader always sees one coherent generation.
    private volatile ExternalProviderSnapshot? _snapshot;

    public ExternalProviderRegistry(IExternalProviderStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalProviderModelRegistration>> ListRegistrationsAsync(CancellationToken ct)
    {
        return (await GetSnapshotAsync(ct).ConfigureAwait(false)).Registrations;
    }

    /// <inheritdoc />
    public async Task<ExternalProviderModelRegistration?> TryResolveAsync(string modelId, CancellationToken ct)
    {
        // Canonicalize FIRST: the caller may hold the id in whatever case the provider map (NOCASE) handed back, while
        // the snapshot is keyed by the one canonical spelling the store minted.
        if (ExternalModelId.Canonicalize(modelId) is not { } canonical)
        {
            return null;
        }

        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.ByModelId.GetValueOrDefault(canonical);
    }

    /// <inheritdoc />
    public async Task<string?> GetApiKeyAsync(string connectionId, CancellationToken ct)
    {
        // Read through the STORE, not the snapshot: the snapshot holds the key-free read model on purpose, so a future
        // consumer that reaches for a cached descriptor cannot find a key on it.
        var config = await _store.LoadAsync(ct).ConfigureAwait(false);
        var canonical = ExternalModelId.CanonicalizeConnectionId(connectionId);
        var connection = config.Connections.FirstOrDefault(candidate => string.Equals(candidate.Id, canonical, StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(connection?.ApiKey) ? null : connection.ApiKey;
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        _snapshot = null;
    }

    /// <inheritdoc />
    public async Task PrimeAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool TryClassifyCached(string modelId, out ExternalProviderModelRegistration? registration)
    {
        registration = null;
        if (_snapshot is not { } snapshot)
        {
            return false;
        }

        if (ExternalModelId.Canonicalize(modelId) is not { } canonical)
        {
            // A malformed id IS a resolved answer: the snapshot is present and the id can never match anything in it.
            return true;
        }

        registration = snapshot.ByModelId.GetValueOrDefault(canonical);
        return true;
    }

    /// <summary>
    ///     Returns the cached generation, rebuilding it when there is none.
    /// </summary>
    /// <remarks>
    ///     Deliberately UNsynchronized. A concurrent burst right after an invalidation can rebuild the snapshot more
    ///     than once, and that is the cheaper failure: the work is one read of a small local file plus a decrypt, each
    ///     rebuild produces an equivalent value, and the last writer wins on a field written as one immutable
    ///     reference. Coordinating it would mean either a disposable lock on a service the container never disposes, or
    ///     a cached <see cref="Lazy{T}" /> task that would memoize a transient IO failure for the process lifetime.
    /// </remarks>
    private async Task<ExternalProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is { } cached)
        {
            return cached;
        }

        var snapshot = ExternalProviderSnapshot.Build(await _store.LoadAsync(cancellationToken).ConfigureAwait(false));
        _snapshot = snapshot;
        return snapshot;
    }

    /// <summary>One coherent generation of the registry: the ordered registrations plus their canonical-id index.</summary>
    private sealed record ExternalProviderSnapshot(IReadOnlyList<ExternalProviderModelRegistration> Registrations,
        FrozenDictionary<string, ExternalProviderModelRegistration> ByModelId)
    {
        public static ExternalProviderSnapshot Build(StoredExternalProviderConfig config)
        {
            var registrations = new List<ExternalProviderModelRegistration>();
            foreach (var connection in config.Connections)
            {
                ExternalProviderConnectionDescriptor descriptor;
                try
                {
                    descriptor = ExternalProviderStore.ToDescriptor(connection);
                }
                catch (Exception exception) when (exception is UriFormatException or ArgumentException)
                {
                    // A stored row whose base URL no longer parses is dropped rather than allowed to fault every
                    // lookup: one hand-edited connection must not take the operator's other connections offline with
                    // it. Its models then resolve to null, which every consumer already treats as fail-closed.
                    continue;
                }

                registrations.AddRange(connection.Models.Select(model =>
                    new ExternalProviderModelRegistration(descriptor, ExternalProviderStore.ToDescriptor(model))));
            }

            // Last write wins on a duplicate id, which the store's per-connection validation already prevents; the
            // tolerant build is what keeps a hand-edited file from faulting the whole registry.
            var index = new Dictionary<string, ExternalProviderModelRegistration>(StringComparer.Ordinal);
            foreach (var registration in registrations)
            {
                index[registration.ModelId] = registration;
            }

            return new ExternalProviderSnapshot(registrations, index.ToFrozenDictionary(StringComparer.Ordinal));
        }
    }
}
