namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>
///     Resolves the active cloud chat client on demand, per request.
///     <b>An explicit per-request model id (<see cref="TryCreateActiveCloudChatClient" />'s <c>requestedModelId</c>,
///     the caller's <c>ChatOptions.ModelId</c>) always takes precedence over the node-default selection</b> — this is
///     what lets the chat model dropdown route ONE send to Azure (or to a specific Codex model) independent of what
///     is signed in / configured as the node default. Precedence when a concrete id is supplied:
///     <list type="number">
///         <item>It matches a stored Azure Foundry deployment name (case-insensitive) → that Azure deployment, even
///         over an active Codex session (switching the model dropdown to Azure must not require signing out of Codex first).</item>
///         <item>Else, a Codex session is present AND the id is a recognized Codex model (<see cref="CodexModelCatalog.IsCodexModel" />) → Codex, built with that model.</item>
///         <item>Else → <see langword="null" /> (route local). This is the ORPHAN GUARD: a stale/unknown id (including a
///         local model name, or an Azure deployment since removed) never silently reaches Codex or Azure — it routes local. Routing
///         decisions here never throw; an unusable but SELECTED provider still throws downstream from <c>CloudSelection.Build()</c>.</item>
///     </list>
///     When <c>requestedModelId</c> is <see langword="null" />/blank (an agent/flow participant with no pinned model), the
///     selection is <b>byte-identical to the pre-per-request-routing behavior</b>: Codex-session-presence-first, else
///     Azure-by-node-default (<see cref="StoredNodeSettings.DefaultModelName" /> matched against the stored connection's
///     deployment names) — this is the node-default backward-compat path.
///     <para>
///         A Codex session is <em>usable</em> when it is non-expired (skew-adjusted) or carries a refresh token the auth
///         handler can rotate; an expired session with no refresh token still selects Codex (never silent-local) but the
///         cloud factory surfaces a typed re-auth error rather than building a doomed client.
///     </para>
///     <para>
///         <b>Per-send caching:</b> re-resolving must be cheap even though the requested model id can differ send to
///         send. Two caches keep the hot path off disk and off rebuilds: (1) a short-TTL <em>store snapshot</em>
///         (Codex session + Azure config + node settings, read together) so the encrypted token-store / credential /
///         node-settings reads are not performed on every send (invalidated immediately on sign-in / sign-out via
///         <see cref="InvalidateSelectionCache" />); the per-request selection is then computed from that snapshot
///         with no further I/O. (2) A client cache keyed on a stable <em>selection identity</em> (the provider plus the
///         resolved model — e.g. "azure:gpt-4o" or "codex:gpt-5.4") so alternating sends between a small set of models
///         (e.g. an Azure deployment for the main assistant and a Codex model for a sub-agent) reuse each model's client
///         rather than rebuilding on every alternation; within an identity, a fingerprint (which also folds in
///         volatile fields — token expiry, Entra settings) decides whether that identity's cached client is still valid
///         or must be rebuilt. The identity keying (rather than the raw fingerprint) keeps the cache bounded — an
///         hourly Codex token refresh replaces that identity's one entry rather than accumulating a new one forever.
///     </para>
///     <para>
///         <b>Swapped-out clients are NOT disposed</b> (concurrency-safety): the singleton chat client
///         is called by parallel requests, and a request may be mid-stream on the previously cached wrapper when the
///         selection flips. The cloud wrappers own nothing real — the HttpClient/handler chain is owned and
///         disposal-protected by the singleton <see cref="ICodexOAuthChatClientFactory" /> / Azure factory — so a
///         swapped-out wrapper is a thin MEAI adapter that the GC reclaims safely. Eagerly disposing it on swap would tear
///         down a client another request is still streaming on (<see cref="ObjectDisposedException" /> / corrupted SSE), so
///         disposal happens only at container shutdown (<see cref="Dispose" />), never on swap. Swaps are rare (sign-in /
///         out / hourly refresh / a model switch), so the transient extra wrapper is negligible.
///     </para>
/// </summary>
public sealed class ActiveCloudChatClientFactory : IActiveCloudChatClientFactory, IDisposable
{
    private const string CodexFingerprintPrefix = "codex";
    private const string AzureFingerprintPrefix = "azure";

    /// <summary>
    ///     How long a resolved store snapshot is reused before the encrypted token-store / credential store / node
    ///     settings are re-read. Short enough that a sign-in/out missed by <see cref="InvalidateSelectionCache" /> still
    ///     takes effect within a couple of sends; long enough to keep the disk reads off a burst of concurrent turns.
    /// </summary>
    private static readonly TimeSpan SelectionCacheTtl = TimeSpan.FromSeconds(3);

    private readonly IAzureFoundryChatClientFactory _azureFactory;

    private readonly Lock _cacheGate = new();

    // Keyed by selection IDENTITY (provider + resolved model), not the raw fingerprint, so the set of live entries
    // stays bounded to the distinct models actually in use (see the class remarks). Never removed/disposed on swap —
    // only replaced — so an in-flight request holding a stale value keeps it alive until the GC reclaims it.
    private readonly ConcurrentDictionary<string, CachedClient> _clientCache = new(StringComparer.Ordinal);

    // Lazy: the Codex chat-client factory owns an HttpClient + CodexAuthHandler chain it builds in its ctor. Taking
    // it lazily keeps that chain from being constructed when this selector is built — which happens eagerly when
    // FastEndpoints instantiates the endpoints at host startup. It materializes only when a Codex client is first
    // built (a real send), so a node with no Codex usage never spins up the transport.
    private readonly Lazy<ICodexOAuthChatClientFactory> _codexFactory;
    private readonly CodexOptions _codexOptions;

    private readonly ICodexTokenStore _codexTokenStore;
    private readonly ICloudCredentialStore _credentialStore;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly TimeProvider _timeProvider;

    // Store-snapshot cache: keeps the per-send token-store / credential-store / node-settings reads off the hot path.
    private StoreSnapshot? _cachedSnapshot;
    private bool _snapshotCacheValid;
    private DateTimeOffset _snapshotCachedAtUtc = DateTimeOffset.MinValue;

    public ActiveCloudChatClientFactory(ICodexTokenStore codexTokenStore,
        ICloudCredentialStore credentialStore,
        IAzureFoundryChatClientFactory azureFactory,
        Lazy<ICodexOAuthChatClientFactory> codexFactory,
        IOptions<CodexOptions> codexOptions,
        INodeSettingsStore nodeSettingsStore,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(codexTokenStore);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(azureFactory);
        ArgumentNullException.ThrowIfNull(codexFactory);
        ArgumentNullException.ThrowIfNull(codexOptions);
        ArgumentNullException.ThrowIfNull(nodeSettingsStore);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _codexTokenStore = codexTokenStore;
        _credentialStore = credentialStore;
        _azureFactory = azureFactory;
        _codexFactory = codexFactory;
        _codexOptions = codexOptions.Value;
        _nodeSettingsStore = nodeSettingsStore;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public bool TryCreateActiveCloudChatClient(string? requestedModelId, out IChatClient? client)
    {
        var selection = BuildSelection(ResolveSnapshot(), requestedModelId);
        if (selection is null)
        {
            client = null;
            return false;
        }

        // Cache hit on an unchanged fingerprint for this selection identity: reuse the existing client (no rebuild,
        // no new transport).
        if (_clientCache.TryGetValue(selection.CacheKey, out var cached)
            && string.Equals(cached.Fingerprint, selection.Fingerprint, StringComparison.Ordinal))
        {
            client = cached.Client;
            return true;
        }

        // Build OUTSIDE any lock. A build can block for a long time (e.g. Entra ID InteractiveBrowserCredential's
        // synchronous first-use sign-in opens a browser and waits on the operator) — serializing concurrent sends
        // behind that would stall unrelated requests. A failed build propagates here WITHOUT touching the cache, so
        // the previous cached entry for this identity survives. Double-checked: a concurrent caller may build and
        // publish an entry for the same identity in the meantime; that duplicate build is wasted work but harmless
        // (last write below wins, both clients are behaviorally equivalent for the same selection).
        var built = selection.Build();

        // Overwrite WITHOUT disposing the old value — see the class remarks (concurrency-safe; GC reclaims it).
        _clientCache[selection.CacheKey] = new CachedClient(selection.Fingerprint, built);

        client = built;
        return true;
    }

    /// <inheritdoc />
    public bool IsCloudProviderSelected(string? requestedModelId = null)
    {
        return BuildSelection(ResolveSnapshot(), requestedModelId) is not null;
    }

    /// <inheritdoc />
    public void InvalidateSelectionCache()
    {
        lock (_cacheGate)
        {
            _snapshotCacheValid = false;
            _cachedSnapshot = null;
        }
    }

    public void Dispose()
    {
        // Container shutdown: nothing is racing us, so it is safe to dispose every cached wrapper.
        foreach (var cached in _clientCache.Values)
        {
            cached.Client.Dispose();
        }

        _clientCache.Clear();
    }

    /// <summary>
    ///     Returns the current store snapshot (Codex session + Azure config + node settings), reading all three
    ///     together and snapshot-caching the result for <see cref="SelectionCacheTtl" /> so the encrypted stores are
    ///     not hit on every send; a sign-in / sign-out invalidates the snapshot immediately via
    ///     <see cref="InvalidateSelectionCache" />. Computing the per-request selection from this snapshot
    ///     (<see cref="BuildSelection" />) performs no further I/O, so a differently-modeled request on an otherwise
    ///     cache-hot send is still free.
    /// </summary>
    private StoreSnapshot ResolveSnapshot()
    {
        var now = _timeProvider.GetUtcNow();

        lock (_cacheGate)
        {
            if (_snapshotCacheValid && now - _snapshotCachedAtUtc < SelectionCacheTtl)
            {
                return _cachedSnapshot!;
            }
        }

        var session = _codexTokenStore.LoadAsync().GetAwaiter().GetResult();
        var config = _credentialStore.LoadConfigAsync().GetAwaiter().GetResult();
        var nodeSettings = _nodeSettingsStore.LoadAsync().GetAwaiter().GetResult();
        var snapshot = new StoreSnapshot(session, config?.AzureFoundry, nodeSettings);

        lock (_cacheGate)
        {
            _cachedSnapshot = snapshot;
            _snapshotCachedAtUtc = now;
            _snapshotCacheValid = true;
        }

        return snapshot;
    }

    /// <summary>
    ///     Pure (no I/O) selection decision over an already-resolved <see cref="StoreSnapshot" />. See the class
    ///     remarks for the full precedence rules.
    /// </summary>
    private CloudSelection? BuildSelection(StoreSnapshot snapshot, string? requestedModelId)
    {
        var trimmedRequested = requestedModelId?.Trim();
        var hasExplicitModel = !string.IsNullOrWhiteSpace(trimmedRequested);

        if (hasExplicitModel)
        {
            var explicitAzure = TryBuildAzureSelection(snapshot.Connection, trimmedRequested);
            if (explicitAzure is not null)
            {
                return explicitAzure;
            }
        }
        else
        {
            // No per-request model (backward compat: an agent/flow participant with no pinned model) → EXACT
            // pre-per-request-routing behavior. Codex takes precedence when a session is present; Azure is
            // selected only when the node default matches one of the connection's deployment names.
            if (snapshot.Session is not null)
            {
                var nodeDefaultCodexModel = CodexModelCatalog.IsCodexModel(snapshot.NodeSettings.DefaultModelName)
                    ? snapshot.NodeSettings.DefaultModelName!
                    : _codexOptions.DefaultModel;
                return BuildCodexSelection(snapshot.Session, nodeDefaultCodexModel);
            }

            return TryBuildAzureSelection(snapshot.Connection, snapshot.NodeSettings.DefaultModelName);
        }

        // An explicit per-request model that did not match an Azure deployment: Codex only when a session is
        // present AND the requested id is itself a recognized Codex model — an active Codex session must never
        // hijack an explicit non-Codex model pick.
        if (snapshot.Session is not null && CodexModelCatalog.IsCodexModel(trimmedRequested))
        {
            return BuildCodexSelection(snapshot.Session, trimmedRequested!);
        }

        // A concrete, non-cloud model id that matched neither an Azure deployment nor the Codex catalog — including
        // a local model name, or a stale/orphaned id (an Azure deployment since removed) — routes local. This never
        // throws; an unusable but SELECTED provider still throws downstream from CloudSelection.Build().
        return null;
    }

    /// <summary>
    ///     Builds an Azure selection when <paramref name="candidateModelId" /> matches one of
    ///     <paramref name="connection" />'s deployment names (case-insensitive); otherwise <see langword="null" />.
    ///     Shared by the explicit per-request path and the node-default fallback — both resolve to the same shape,
    ///     they differ only in which model id is being matched.
    /// </summary>
    private CloudSelection? TryBuildAzureSelection(StoredAzureFoundryConnection? connection, string? candidateModelId)
    {
        if (connection is not { Models.Count: > 0 } || string.IsNullOrWhiteSpace(candidateModelId))
        {
            return null;
        }

        var matchedDeployment = connection.Models
                                          .FirstOrDefault(model => string.Equals(model.DeploymentName, candidateModelId, StringComparison.OrdinalIgnoreCase))
                                          ?.DeploymentName;
        if (string.IsNullOrWhiteSpace(matchedDeployment))
        {
            return null;
        }

        // Folds every Entra ID field too (tenant/client/secret-length/scope/sign-in method) so an operator edit to
        // any of them — including a fresh device-code sign-in that changes nothing here but is followed by a
        // settings save — rebuilds the cached client rather than reusing a stale credential.
        var fingerprint = string.Create(CultureInfo.InvariantCulture,
            $"{AzureFingerprintPrefix}|{connection.Endpoint}|{connection.AuthMode}|{matchedDeployment}|{connection.ApiKey?.Length ?? 0}" +
            $"|{connection.EntraTenantId}|{connection.EntraClientId}|{connection.EntraClientSecret?.Length ?? 0}" +
            $"|{connection.EntraTokenScope}|{connection.EntraSignInMethod}");
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{AzureFingerprintPrefix}|{matchedDeployment}");

        return new CloudSelection(cacheKey, fingerprint, () => _azureFactory.Create(connection, matchedDeployment));
    }

    /// <summary>Builds a Codex selection for the given session and the (already-resolved) model id to build it with.</summary>
    private CloudSelection BuildCodexSelection(CodexTokens session, string modelId)
    {
        // The fingerprint folds in the expiry tick count AND the model, so a refresh OR a model switch rebuilds the
        // client; a re-login changes the account id. If the session is unusable (expired AND no refresh token) the
        // Codex factory surfaces AuthRequired.
        var fingerprint = string.Create(CultureInfo.InvariantCulture,
            $"{CodexFingerprintPrefix}|{session.AccountId}|{session.ExpiresUtc.UtcTicks}|{modelId}");
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{CodexFingerprintPrefix}|{modelId}");

        return new CloudSelection(cacheKey, fingerprint, () => _codexFactory.Value.Create(modelId));
    }

    /// <summary>A resolved cloud selection: its identity cache key, its (more granular) fingerprint, and a deferred client builder.</summary>
    private sealed record CloudSelection(string CacheKey, string Fingerprint, Func<IChatClient> Build);

    /// <summary>A cached client for a selection identity, alongside the fingerprint it was built for.</summary>
    private sealed record CachedClient(string Fingerprint, IChatClient Client);

    /// <summary>The three stores' state as of one read, cached together for <see cref="SelectionCacheTtl" />.</summary>
    private sealed record StoreSnapshot(CodexTokens? Session, StoredAzureFoundryConnection? Connection, StoredNodeSettings NodeSettings);
}
