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
///     Resolves the active cloud chat client on demand, per request: an explicit <c>ChatOptions.ModelId</c> always takes
///     precedence over the node-default selection.
/// </summary>
/// <remarks>
///     For a concrete id the order is: a stored Azure Foundry deployment name (case-insensitive), even over an active Codex
///     session; else a present Codex session with a recognized <see cref="CodexModelCatalog.IsCodexModel" /> id; else
///     <see langword="null" /> — the ORPHAN GUARD, so a stale or unknown id never silently reaches Codex or Azure. Routing
///     never throws here; a SELECTED but unusable provider throws from <c>CloudSelection.Build()</c>. Swapped-out clients are
///     replaced, never disposed. Caching and the node-default path: docs/wiki/03-local-runtime-and-providers.md, "The active cloud selection".
/// </remarks>
public sealed class ActiveCloudChatClientFactory : IActiveCloudChatClientFactory, IDisposable
{
    private const string CodexFingerprintPrefix = "codex";
    private const string AzureFingerprintPrefix = "azure";

    /// <summary>
    ///     How long a resolved store snapshot is reused before the encrypted token-store / credential store / node
    ///     settings are re-read.
    /// </summary>
    /// <remarks>
    ///     Short enough that a sign-in/out missed by <see cref="InvalidateSelectionCache" /> still takes effect within a
    ///     couple of sends; long enough to keep the disk reads off a burst of concurrent turns.
    /// </remarks>
    private static readonly TimeSpan SelectionCacheTtl = TimeSpan.FromSeconds(3);

    private readonly IAzureFoundryChatClientFactory _azureFactory;

    private readonly Lock _cacheGate = new();

    /// <summary>Keyed by selection IDENTITY (provider + resolved model), not the raw fingerprint, so live entries stay bounded to the models actually in use.</summary>
    /// <remarks>Entries are replaced, never removed or disposed on swap, so an in-flight request holding a stale value keeps it alive until the GC reclaims it.</remarks>
    private readonly ConcurrentDictionary<string, CachedClient> _clientCache = new(StringComparer.Ordinal);

    /// <summary>Lazy, so the Codex chat-client factory's ctor-built HttpClient + CodexAuthHandler chain is not constructed when this selector is.</summary>
    /// <remarks>
    ///     This selector is built eagerly, when FastEndpoints instantiates the endpoints at host startup. The chain
    ///     materializes only when a Codex client is first built (a real send), so a node with no Codex usage never spins
    ///     up the transport.
    /// </remarks>
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

        // Build OUTSIDE any lock: a build can block for a long time (Entra's InteractiveBrowserCredential opens a browser and waits on the operator),
        // and a failure propagates without touching the cache, so this identity's previous entry survives. A concurrent duplicate build is wasted but harmless.
        var built = selection.Build();

        // Overwrite WITHOUT disposing the old value — see the class remarks (concurrency-safe; GC reclaims it).
        _clientCache[selection.CacheKey] = new CachedClient { Fingerprint = selection.Fingerprint, Client = built };

        client = built;
        return true;
    }

    /// <inheritdoc />
    public bool IsCloudProviderSelected(string? requestedModelId = null)
    {
        return BuildSelection(ResolveSnapshot(), requestedModelId) is not null;
    }

    /// <inheritdoc />
    public string? ResolveActiveCloudProviderName(string? requestedModelId = null)
    {
        // Same pure selection decision as IsCloudProviderSelected, but returns WHICH cloud provider ("codex"/"azure")
        // rather than a bool. Snapshot-cached, no network I/O. Null when no cloud provider is selected (routes local).
        return BuildSelection(ResolveSnapshot(), requestedModelId)?.ProviderName;
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
    ///     together.
    /// </summary>
    /// <remarks>
    ///     The result is cached for <see cref="SelectionCacheTtl" /> so the encrypted stores are not hit on every send;
    ///     a sign-in / sign-out invalidates it immediately via <see cref="InvalidateSelectionCache" />. Computing the
    ///     per-request selection from this snapshot (<see cref="BuildSelection" />) performs no further I/O, so a
    ///     differently-modeled request on an otherwise cache-hot send is still free.
    /// </remarks>
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

        // Forced synchronous: this chain is reached from Microsoft.Extensions.AI IChatClient.GetService via RuntimeChatClient.ResolveActiveClient,
        // and GetService has no async overload on that interface. See docs/wiki/16-code-conventions.md ("Blocking calls and cancellation forwarding").
#pragma warning disable MA0045, MA0032 // forced sync by IChatClient.GetService (see comment above)
        var session = _codexTokenStore.LoadAsync().GetAwaiter().GetResult();
        var config = _credentialStore.LoadConfigAsync().GetAwaiter().GetResult();
        var nodeSettings = _nodeSettingsStore.LoadAsync().GetAwaiter().GetResult();
#pragma warning restore MA0045, MA0032
        var snapshot = new StoreSnapshot { Session = session, Connection = config?.AzureFoundry, NodeSettings = nodeSettings };

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
            // No per-request model (an agent/flow participant with no pinned model): Codex takes precedence when a session
            // is present, and Azure is selected only when the node default matches one of the connection's deployment names.
            if (snapshot.Session is not null)
            {
                var nodeDefaultCodexModel = CodexModelCatalog.IsCodexModel(snapshot.NodeSettings.DefaultModelName)
                    ? snapshot.NodeSettings.DefaultModelName!
                    : _codexOptions.DefaultModel;
                return BuildCodexSelection(snapshot.Session, nodeDefaultCodexModel);
            }

            return TryBuildAzureSelection(snapshot.Connection, snapshot.NodeSettings.DefaultModelName);
        }

        // An explicit per-request model that matched no Azure deployment: Codex only when a session is present AND the
        // requested id is itself a recognized Codex model — an active Codex session must never hijack a non-Codex pick.
        if (snapshot.Session is not null && CodexModelCatalog.IsCodexModel(trimmedRequested))
        {
            return BuildCodexSelection(snapshot.Session, trimmedRequested!);
        }

        // A concrete non-cloud id that matched neither an Azure deployment nor the Codex catalog — a local model name, or a
        // stale id such as an Azure deployment since removed — routes local. Never throws; a SELECTED but unusable provider throws from CloudSelection.Build().
        return null;
    }

    /// <summary>
    ///     Builds an Azure selection when <paramref name="candidateModelId" /> matches one of
    ///     <paramref name="connection" />'s deployment names (case-insensitive); otherwise <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Shared by the explicit per-request path and the node-default fallback: both resolve to the same shape and
    ///     differ only in which model id is matched.
    /// </remarks>
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

        // Folds every Entra ID field too (tenant/client/secret-length/scope/sign-in method), so an operator edit to any of
        // them — including a fresh device-code sign-in followed by a settings save — rebuilds the cached client instead of reusing a stale credential.
        var fingerprint = string.Create(CultureInfo.InvariantCulture,
            $"{AzureFingerprintPrefix}|{connection.Endpoint}|{connection.AuthMode}|{matchedDeployment}|{connection.ApiKey?.Length ?? 0}" +
            $"|{connection.EntraTenantId}|{connection.EntraClientId}|{connection.EntraClientSecret?.Length ?? 0}" +
            $"|{connection.EntraTokenScope}|{connection.EntraSignInMethod}");
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{AzureFingerprintPrefix}|{matchedDeployment}");

        return new CloudSelection { CacheKey = cacheKey, Fingerprint = fingerprint, ProviderName = AzureFingerprintPrefix, Build = () => _azureFactory.Create(connection, matchedDeployment) };
    }

    /// <summary>Builds a Codex selection for the given session and the (already-resolved) model id to build it with.</summary>
    private CloudSelection BuildCodexSelection(CodexTokens session, string modelId)
    {
        // The fingerprint folds in the expiry tick count AND the model, so a refresh or a model switch rebuilds the client,
        // while a re-login changes the account id. An unusable session (expired AND no refresh token) makes the Codex factory surface AuthRequired.
        var fingerprint = string.Create(CultureInfo.InvariantCulture,
            $"{CodexFingerprintPrefix}|{session.AccountId}|{session.ExpiresUtc.UtcTicks}|{modelId}");
        var cacheKey = string.Create(CultureInfo.InvariantCulture, $"{CodexFingerprintPrefix}|{modelId}");

        return new CloudSelection { CacheKey = cacheKey, Fingerprint = fingerprint, ProviderName = CodexFingerprintPrefix, Build = () => _codexFactory.Value.Create(modelId) };
    }

    /// <summary>
    ///     A resolved cloud selection: its identity cache key, its (more granular) fingerprint, the fine-grained provider
    ///     name (<see cref="CodexFingerprintPrefix" /> / <see cref="AzureFingerprintPrefix" />), and a deferred client builder.
    /// </summary>
    private sealed record CloudSelection
    {
        public required string CacheKey { get; init; }

        public required string Fingerprint { get; init; }

        public required string ProviderName { get; init; }

        public required Func<IChatClient> Build { get; init; }
    }

    /// <summary>A cached client for a selection identity, alongside the fingerprint it was built for.</summary>
    private sealed record CachedClient
    {
        public required string Fingerprint { get; init; }

        public required IChatClient Client { get; init; }
    }

    /// <summary>The three stores' state as of one read, cached together for <see cref="SelectionCacheTtl" />.</summary>
    private sealed record StoreSnapshot
    {
        public required CodexTokens? Session { get; init; }

        public required StoredAzureFoundryConnection? Connection { get; init; }

        public required StoredNodeSettings NodeSettings { get; init; }
    }
}
