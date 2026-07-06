namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>
///     Resolves the active cloud chat client on demand.
///     <b>Codex selection keys off Codex-session presence</b> — a live OAuth session in the separate encrypted
///     <see cref="ICodexTokenStore" />. <b>Azure selection is selected-model-driven</b> (HIGH-1): an Azure connection
///     is selected only when the node-default model id matches one of the stored connection's deployment names; a saved
///     Azure connection alone never forces cloud, so selecting a local model still routes local.
///     <para>
///         Codex takes precedence when a session is present (the session branch resolves first); signing out (clearing
///         the session) reverts to Azure-or-local on the next send (the runtime-switch property).
///         A Codex session is <em>usable</em> when it is non-expired (skew-adjusted) or carries a refresh token the auth
///         handler can rotate; an expired session with no refresh token still selects Codex (never silent-local) but the
///         cloud factory surfaces a typed re-auth error rather than building a doomed client.
///     </para>
///     <para>
///         <b>Per-send caching:</b> re-resolving must be cheap. Two caches keep the hot path off disk and off rebuilds:
///         (1) a short-TTL <em>selection snapshot</em> so the encrypted token-store / credential read is not performed on
///         every send (invalidated immediately on sign-in / sign-out via <see cref="InvalidateSelectionCache" />); and
///         (2) a <em>client cache</em> keyed on a selection fingerprint so the OpenAI/Codex client is rebuilt only when
///         the selection actually changes (sign-in / sign-out / token refresh / Azure settings change).
///     </para>
///     <para>
///         <b>Swapped-out clients are NOT disposed</b> (concurrency-safety): the singleton chat client
///         is called by parallel requests, and a request may be mid-stream on the previously cached wrapper when the
///         selection flips. The cloud wrappers own nothing real — the HttpClient/handler chain is owned and
///         disposal-protected by the singleton <see cref="ICodexOAuthChatClientFactory" /> / Azure factory — so a
///         swapped-out wrapper is a thin MEAI adapter that the GC reclaims safely. Eagerly disposing it on swap would tear
///         down a client another request is still streaming on (<see cref="ObjectDisposedException" /> / corrupted SSE), so
///         disposal happens only at container shutdown (<see cref="Dispose" />), never on swap. Swaps are rare (sign-in /
///         out / hourly refresh), so the transient extra wrapper is negligible.
///     </para>
/// </summary>
public sealed class ActiveCloudChatClientFactory : IActiveCloudChatClientFactory, IDisposable
{
    private const string CodexFingerprintPrefix = "codex";
    private const string AzureFingerprintPrefix = "azure";

    /// <summary>
    ///     How long a resolved selection snapshot is reused before the encrypted token-store / credential store is
    ///     re-read. Short enough that a sign-in/out missed by <see cref="InvalidateSelectionCache" /> still takes
    ///     effect within a couple of sends; long enough to keep the disk read off a burst of concurrent turns.
    /// </summary>
    private static readonly TimeSpan SelectionCacheTtl = TimeSpan.FromSeconds(3);

    private readonly IAzureFoundryChatClientFactory _azureFactory;

    private readonly Lock _cacheGate = new();

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
    private IChatClient? _cachedClient;
    private string? _cachedFingerprint;

    // Selection snapshot cache: keeps the per-send token-store read + DataProtection off the hot path.
    private CloudSelection? _cachedSelection;
    private bool _selectionCacheValid;
    private DateTimeOffset _selectionCachedAtUtc = DateTimeOffset.MinValue;

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
    public bool TryCreateActiveCloudChatClient(out IChatClient? client)
    {
        var selection = ResolveSelection();
        if (selection is null)
        {
            // No cloud provider selected → route local. Forget any stale cached cloud client (do NOT dispose it —
            // a concurrent request may still be streaming on it; the GC reclaims the orphaned wrapper).
            ForgetCachedClient();
            client = null;
            return false;
        }

        lock (_cacheGate)
        {
            // Cache hit on an unchanged selection: reuse the existing client (no rebuild, no new transport).
            if (_cachedClient is not null && string.Equals(_cachedFingerprint, selection.Fingerprint, StringComparison.Ordinal))
            {
                client = _cachedClient;
                return true;
            }
        }

        // Build OUTSIDE the lock. A build can block for a long time (e.g. Entra ID InteractiveBrowserCredential's
        // synchronous first-use sign-in opens a browser and waits on the operator) — holding _cacheGate across that
        // call would serialize every other concurrent send behind it. A failed build propagates here WITHOUT
        // touching the cache, so the previous cached client survives. Double-checked: a concurrent caller may build
        // and publish a client for the same fingerprint in the meantime; that duplicate build is wasted work but
        // harmless (last write below wins, both clients are behaviorally equivalent for the same selection).
        var built = selection.Build();

        lock (_cacheGate)
        {
            // Swap WITHOUT disposing the old wrapper — see the class remarks (concurrency-safe; GC reclaims it).
            _cachedClient = built;
            _cachedFingerprint = selection.Fingerprint;
        }

        client = built;
        return true;
    }

    /// <inheritdoc />
    public bool IsCloudProviderSelected()
    {
        return ResolveSelection() is not null;
    }

    /// <inheritdoc />
    public void InvalidateSelectionCache()
    {
        lock (_cacheGate)
        {
            _selectionCacheValid = false;
            _cachedSelection = null;
        }
    }

    public void Dispose()
    {
        // Container shutdown: nothing is racing us, so it is safe to dispose the final cached wrapper.
        lock (_cacheGate)
        {
            _cachedClient?.Dispose();
            _cachedClient = null;
            _cachedFingerprint = null;
        }
    }

    /// <summary>
    ///     Returns the current selection (fingerprint + deferred builder), or <see langword="null" /> when no cloud
    ///     provider is selected. Codex takes precedence over Azure. The result is snapshot-cached for
    ///     <see cref="SelectionCacheTtl" /> to keep the encrypted token-store / credential read off every send; a
    ///     sign-in / sign-out invalidates the snapshot immediately via <see cref="InvalidateSelectionCache" />.
    /// </summary>
    private CloudSelection? ResolveSelection()
    {
        var now = _timeProvider.GetUtcNow();

        lock (_cacheGate)
        {
            if (_selectionCacheValid && now - _selectionCachedAtUtc < SelectionCacheTtl)
            {
                return _cachedSelection;
            }
        }

        var selection = ReadSelectionFromStores();

        lock (_cacheGate)
        {
            _cachedSelection = selection;
            _selectionCachedAtUtc = now;
            _selectionCacheValid = true;
        }

        return selection;
    }

    private CloudSelection? ReadSelectionFromStores()
    {
        var session = _codexTokenStore.LoadAsync().GetAwaiter().GetResult();
        if (session is not null)
        {
            // A present session always means Codex is selected (never silent-local while a session file exists).
            // Resolve the selected Codex model: the operator's node-default selection when it is a valid Codex model
            // id, else the configured Codex default. A non-Codex selection (e.g. a local Ollama model name left in
            // DefaultModelName) must NOT reach Codex as the model — fall back to the default so the Codex client is
            // always built with a valid id (the 400 cause). The per-call ModelId is additionally pinned at the Codex
            // boundary (CodexStoreDisabledChatClient), so this only chooses WHICH valid Codex model.
            var selectedModel = ResolveSelectedCodexModel();

            // The fingerprint folds in the expiry tick count AND the selected model, so a refresh OR a Codex-model
            // switch rebuilds the client; a re-login changes the account id. If the session is unusable (expired AND
            // no refresh token) the Codex factory surfaces AuthRequired.
            var fingerprint = string.Create(CultureInfo.InvariantCulture,
                $"{CodexFingerprintPrefix}|{session.AccountId}|{session.ExpiresUtc.UtcTicks}|{selectedModel}");
            return new CloudSelection(fingerprint, () => _codexFactory.Value.Create(selectedModel));
        }

        // Azure routing is SELECTED-MODEL-DRIVEN (HIGH-1), NOT connection-presence-driven: a saved Azure connection
        // alone never forces cloud. Return an Azure selection ONLY when the node-default selection matches one of the
        // connection's deployment names. Anything else — a local model selected, or a stale node-default that was an
        // Azure deployment since removed (orphan guard) — yields null so the send routes local. This runs only when no
        // Codex session is present (the session branch above returns first), so a Codex session still takes precedence.
        var config = _credentialStore.LoadConfigAsync().GetAwaiter().GetResult();
        var connection = config?.AzureFoundry;
        if (connection is { Models.Count: > 0 })
        {
            var selectedModel = ResolveSelectedModelName();
            var matchedDeployment = connection.Models
                                              .FirstOrDefault(model => string.Equals(model.DeploymentName, selectedModel, StringComparison.OrdinalIgnoreCase))
                                              ?.DeploymentName;

            if (!string.IsNullOrWhiteSpace(matchedDeployment))
            {
                // Folds every Entra ID field too (tenant/client/secret-length/scope/sign-in method) so an operator
                // edit to any of them — including a fresh device-code sign-in that changes nothing here but is
                // followed by a settings save — rebuilds the cached client rather than reusing a stale credential.
                var fingerprint = string.Create(CultureInfo.InvariantCulture,
                    $"{AzureFingerprintPrefix}|{connection.Endpoint}|{connection.AuthMode}|{matchedDeployment}|{connection.ApiKey?.Length ?? 0}" +
                    $"|{connection.EntraTenantId}|{connection.EntraClientId}|{connection.EntraClientSecret?.Length ?? 0}" +
                    $"|{connection.EntraTokenScope}|{connection.EntraSignInMethod}");
                return new CloudSelection(fingerprint, () => _azureFactory.Create(connection, matchedDeployment));
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns the operator's node-default selected model id (<see cref="StoredNodeSettings.DefaultModelName" />),
    ///     or null when none is set. Used to decide whether the selected model is an Azure deployment.
    /// </summary>
    private string? ResolveSelectedModelName()
    {
        var settings = _nodeSettingsStore.LoadAsync().GetAwaiter().GetResult();
        return settings.DefaultModelName;
    }

    /// <summary>
    ///     Returns the Codex model id to build the client with: the operator's node-default selection
    ///     (<see cref="StoredNodeSettings.DefaultModelName" />) when it is a recognized Codex cloud model id, otherwise
    ///     the configured Codex default (<see cref="CodexOptions.DefaultModel" />). This guarantees the Codex client is
    ///     always constructed with a valid Codex model — a local model name left in the node default never reaches the
    ///     Codex backend (which is what caused the unknown-model HTTP 400).
    /// </summary>
    private string ResolveSelectedCodexModel()
    {
        var settings = _nodeSettingsStore.LoadAsync().GetAwaiter().GetResult();
        return CodexModelCatalog.IsCodexModel(settings.DefaultModelName)
            ? settings.DefaultModelName!
            : _codexOptions.DefaultModel;
    }

    private void ForgetCachedClient()
    {
        lock (_cacheGate)
        {
            // Null the reference only — never Dispose here (a concurrent request may still hold/stream this client).
            _cachedClient = null;
            _cachedFingerprint = null;
        }
    }

    /// <summary>A resolved cloud selection: its identity fingerprint plus a deferred client builder.</summary>
    private sealed record CloudSelection(string Fingerprint, Func<IChatClient> Build);
}
