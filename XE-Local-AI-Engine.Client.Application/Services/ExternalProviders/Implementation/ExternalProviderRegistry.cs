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
    private readonly Lock _publishGate = new();
    private readonly IExternalProviderStore _store;

    // The monotonic epoch every snapshot is stamped with. Bumped by Invalidate BEFORE the snapshot is dropped, so a
    // load already in flight can tell that its result is stale by the time it tries to publish.
    private long _epoch;

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
    public async Task<ExternalProviderBinding?> TryResolveBindingAsync(string modelId, CancellationToken ct)
    {
        return (await TryResolveTransportBindingAsync(modelId, ct).ConfigureAwait(false))?.Binding;
    }

    /// <inheritdoc />
    public async Task<ExternalProviderTransportBinding?> TryResolveTransportBindingAsync(string modelId, CancellationToken ct)
    {
        if (ExternalModelId.Canonicalize(modelId) is not { } canonical)
        {
            return null;
        }

        // ONE snapshot read serves the endpoint, the trust declaration, the generation AND the key. Reading the key
        // through a second call — the shape this replaced — is what let a concurrent edit bind a new key to an old
        // base URL: two reads, two generations, one request.
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.ByModelId.GetValueOrDefault(canonical) is not { } registration)
        {
            return null;
        }

        var apiKey = snapshot.KeysByConnectionId.GetValueOrDefault(registration.Connection.Id);
        return new ExternalProviderTransportBinding(new ExternalProviderBinding(snapshot.Generation, registration), apiKey);
    }

    /// <inheritdoc />
    public void Invalidate()
    {
        lock (_publishGate)
        {
            // Bump FIRST, drop second. A load that started before this call carries the pre-bump epoch and is refused
            // publication below, so it can never resurrect a configuration the operator has already replaced.
            _epoch++;
            _snapshot = null;
        }
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
    ///     <para>
    ///         The LOAD is deliberately unsynchronized. A concurrent burst right after an invalidation can read the
    ///         file more than once, and that is the cheaper failure: the work is one read of a small local file plus a
    ///         decrypt, and holding a lock across it would serialize every cold chat client behind disk I/O.
    ///     </para>
    ///     <para>
    ///         The PUBLICATION is not. Each load stamps the epoch it observed BEFORE reading, and publishes only if the
    ///         epoch has not moved since — so a load that overlapped an <see cref="Invalidate" /> is discarded instead
    ///         of overwriting the newer configuration with the one the operator just replaced. That race is not
    ///         theoretical on this path: a save invalidates while in-flight sends are resolving.
    ///     </para>
    /// </remarks>
    private async Task<ExternalProviderSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is { } cached)
        {
            return cached;
        }

        long observedEpoch;
        lock (_publishGate)
        {
            observedEpoch = _epoch;
        }

        var loaded = ExternalProviderSnapshot.Build(observedEpoch, await _store.LoadAsync(cancellationToken).ConfigureAwait(false));

        lock (_publishGate)
        {
            if (_epoch != observedEpoch)
            {
                // Superseded while we were reading. Return the value we built — the caller asked a question and this is
                // an honest answer to it, stamped with the generation it came from — but do NOT cache it.
                return loaded;
            }

            // Another loader may have published an equivalent snapshot at this same epoch; keeping theirs avoids
            // handing two callers two different (equal) instances for no reason.
            _snapshot ??= loaded;
            return _snapshot;
        }
    }

    /// <summary>
    ///     One coherent generation of the registry: the ordered registrations, their canonical-id index, and the
    ///     connection keys.
    /// </summary>
    /// <remarks>
    ///     The keys live HERE, beside the descriptors built from the same load, rather than being re-read from the
    ///     store on demand — that is what makes an endpoint and its credential structurally incapable of coming from
    ///     two different generations. They are never projected onto a descriptor, so the key-free read model every
    ///     catalog, UI and policy consumer sees is unchanged.
    /// </remarks>
    private sealed record ExternalProviderSnapshot(
        long Generation,
        IReadOnlyList<ExternalProviderModelRegistration> Registrations,
        FrozenDictionary<string, ExternalProviderModelRegistration> ByModelId,
        FrozenDictionary<string, string> KeysByConnectionId)
    {
        public static ExternalProviderSnapshot Build(long generation, StoredExternalProviderConfig config)
        {
            // Shared with the reconciler (see ExternalProviderConfigProjection): the pass that DELETES drift derives
            // its registration set from the configuration it authoritatively loaded, and it must be the same
            // projection this cache is built from or the two would disagree about what is registered.
            var (registrations, keys) = ExternalProviderConfigProjection.Project(config);

            // Last write wins on a duplicate id, which the store's per-connection validation already prevents; the
            // tolerant build is what keeps a hand-edited file from faulting the whole registry.
            var index = new Dictionary<string, ExternalProviderModelRegistration>(StringComparer.Ordinal);
            foreach (var registration in registrations)
            {
                index[registration.ModelId] = registration;
            }

            return new ExternalProviderSnapshot(generation,
                registrations,
                index.ToFrozenDictionary(StringComparer.Ordinal),
                keys.ToFrozenDictionary(StringComparer.Ordinal));
        }
    }
}
