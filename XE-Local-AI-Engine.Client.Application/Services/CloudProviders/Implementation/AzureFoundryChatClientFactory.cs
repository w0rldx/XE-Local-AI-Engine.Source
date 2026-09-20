namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Auth;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Azure Foundry chat-client factory backed by the Azure OpenAI .NET client.
/// </summary>
/// <remarks>
///     The rest of the node runtime consumes the returned <see cref="IChatClient" /> abstraction, which lets local
///     Ollama and cloud-backed deployments share the same agent pipeline. The returned client is wrapped in
///     <see cref="AzureFoundryErrorTranslatingChatClient" /> so an Azure <c>RequestFailedException</c> (content filter,
///     auth) surfaces as a typed <see cref="AzureFoundryProviderException" /> with a sanitized message.
/// </remarks>
public sealed class AzureFoundryChatClientFactory : IAzureFoundryChatClientFactory
{
    /// <summary>
    ///     Placeholder key material for the Azure-deployments surface ONLY: <c>AzureOpenAIClient</c>'s ctor requires an
    ///     <c>ApiKeyCredential</c> even when the real auth is Entra ID.
    /// </summary>
    /// <remarks>
    ///     <see cref="EntraBearerTokenPipelinePolicy" /> is registered at <see cref="PipelinePosition.PerCall" /> there and overwrites
    ///     <c>Authorization</c> on every call, so this placeholder is harmless noise. The v1 surface must NEVER carry it: its SDK-internal auth policy
    ///     sits in a FIXED slot after every PerCall policy, and a placeholder there reached a live gateway as the token ("JWT must have three segments").
    ///     The v1 builders construct the client with a real <c>AuthenticationPolicy</c> instead — docs/wiki/03-local-runtime-and-providers.md, "Azure Foundry: the two wire surfaces".
    /// </remarks>
    private const string PlaceholderApiKey = "unused-entra-id-auth";
    private const string EntraTokenCachePersistenceName = "XE-Local-AI-Engine.Client.AzureFoundry.EntraId";

    // The v1 surface path segment appended to the connection endpoint (Locked v1 surface contract: no api-version
    // query param, trailing slash so the OpenAI SDK's own relative-path joining lands on .../openai/v1/chat/completions).
    private const string OpenAiV1PathSegment = "/openai/v1/";

    /// <summary>Documented Entra ID scope for the v1 surface under managed-identity auth (Microsoft Learn, 2026-06).</summary>
    /// <remarks>
    ///     The ApiKey / EntraId auth modes carry their own scope (the API key itself, or the operator-configured
    ///     <c>EntraTokenScope</c>), so this constant applies to ManagedIdentity only.
    /// </remarks>
    private const string ManagedIdentityV1Scope = "https://ai.azure.com/.default";

    /// <summary>Client-credentials (app-only) token requests are rejected by Entra ID (AADSTS1002012) unless the scope ends in this suffix.</summary>
    /// <remarks>
    ///     A delegated scope such as <c>api://&lt;app-id-uri&gt;/access_as_user</c> only works with a user-delegated flow
    ///     (device-code / interactive browser). The scope is intentionally never auto-rewritten: there is no safe general
    ///     rule for a multi-segment App-ID-URI or a trailing-slash host, so a mismatch fails fast instead.
    /// </remarks>
    private const string ClientCredentialsScopeSuffix = "/.default";

    /// <summary>Upper bound on how long a live interactive-browser sign-in may block the send that triggered it.</summary>
    /// <remarks>
    ///     MSAL's system-browser flow waits on a localhost redirect that never arrives when the operator closes the
    ///     window without completing sign-in, so an uncapped <c>Authenticate()</c> blocks that send forever. Five minutes
    ///     leaves room for MFA while still guaranteeing the send eventually fails with a typed, retryable error.
    /// </remarks>
    private static readonly TimeSpan InteractiveBrowserSignInTimeout = TimeSpan.FromMinutes(5);

    private readonly IEntraAuthCodeAccountStore? _entraAuthCodeAccountStore;
    private readonly IEntraLiveCredentialCache? _entraLiveCredentialCache;
    private readonly IEntraTokenCacheStore? _entraTokenCacheStore;
    private readonly ILogger<AzureFoundryChatClientFactory> _logger;
    private readonly INodeDataDirectory? _nodeDataDirectory;

    // 1 while a live interactive-browser prompt is in flight (single-flight gate; this factory is a DI singleton).
    private int _interactiveBrowserSignInInFlight;

    public AzureFoundryChatClientFactory(IEntraTokenCacheStore? entraTokenCacheStore = null,
        IEntraLiveCredentialCache? entraLiveCredentialCache = null,
        IEntraAuthCodeAccountStore? entraAuthCodeAccountStore = null,
        INodeDataDirectory? nodeDataDirectory = null,
        ILogger<AzureFoundryChatClientFactory>? logger = null)
    {
        _entraTokenCacheStore = entraTokenCacheStore;
        _entraLiveCredentialCache = entraLiveCredentialCache;
        _entraAuthCodeAccountStore = entraAuthCodeAccountStore;
        _nodeDataDirectory = nodeDataDirectory;
        _logger = logger ?? NullLogger<AzureFoundryChatClientFactory>.Instance;
    }

    /// <inheritdoc />
    public IChatClient Create(StoredAzureFoundryConnection connection, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An Azure Foundry deployment name must be provided.");
        }

        var endpoint = ResolveEndpoint(connection.Endpoint, connection.AdditionalAllowedHostSuffixes);

        var innerClient = connection.ApiSurface == AzureFoundryApiSurface.OpenAiV1
            ? BuildOpenAiV1Client(endpoint, connection).GetChatClient(deploymentName).AsIChatClient()
            : BuildAzureDeploymentsClient(endpoint, connection).GetChatClient(deploymentName).AsIChatClient();

        return new AzureFoundryErrorTranslatingChatClient(innerClient);
    }

    // The classic Azure deployments surface (default, ApiSurface.AzureDeployments): {endpoint}/openai/deployments/{deployment}/....
    private AzureOpenAIClient BuildAzureDeploymentsClient(Uri endpoint, StoredAzureFoundryConnection connection)
    {
        var options = BuildClientOptions(connection.Headers);

        return connection.AuthMode switch
        {
            AzureFoundryAuthMode.ApiKey => BuildKeyCredentialClient(endpoint, connection.ApiKey, options),
            AzureFoundryAuthMode.ManagedIdentity => new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), options),
            AzureFoundryAuthMode.EntraId => BuildEntraIdClient(endpoint, connection, options),
            _ => throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry connection has an unsupported authentication mode.")
        };
    }

    /// <summary>
    ///     Builds the OpenAI-compatible v1 surface client (<c>ApiSurface.OpenAiV1</c>):
    ///     <c>{endpoint}/openai/v1/chat/completions</c>, deployment name in the request body's <c>model</c> field.
    /// </summary>
    /// <param name="transportHttpClient">
    ///     Test-only seam, <see langword="null" /> in the production <c>Create()</c> path: it points the assembled
    ///     client's transport at a capturing fake instead of the network.
    /// </param>
    /// <remarks>
    ///     The same <see cref="ResolveEndpoint" /> validation and credential shapes as the Azure deployments surface apply,
    ///     wired through <c>OpenAIClientOptions</c> instead of <c>AzureOpenAIClientOptions</c>. The transport seam exists so a
    ///     pipeline-EXECUTION test can assert on the real outbound headers and URI rather than on the wiring that produced
    ///     them: the bug class behind <see cref="PlaceholderApiKey" /> was invisible to construction-only tests.
    /// </remarks>
    private OpenAIClient BuildOpenAiV1Client(Uri endpoint, StoredAzureFoundryConnection connection, HttpClient? transportHttpClient = null)
    {
        var v1Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + OpenAiV1PathSegment);
        var options = new OpenAIClientOptions
        {
            Endpoint = v1Endpoint
        };
        AddCustomHeaderPolicy(options, connection.Headers);
        if (transportHttpClient is not null)
        {
            options.Transport = new HttpClientPipelineTransport(transportHttpClient);
        }

        return connection.AuthMode switch
        {
            AzureFoundryAuthMode.ApiKey => BuildOpenAiV1KeyCredentialClient(connection.ApiKey, options),
            AzureFoundryAuthMode.ManagedIdentity => BuildOpenAiV1EntraClient(new DefaultAzureCredential(), ManagedIdentityV1Scope, options),
            AzureFoundryAuthMode.EntraId => BuildOpenAiV1EntraIdClient(connection, options),
            _ => throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry connection has an unsupported authentication mode.")
        };
    }

    /// <summary>Test-only seam: assembles the SAME v1 client construction path <c>Create()</c> uses, with an injected transport.</summary>
    /// <remarks>
    ///     A request can then be fired through the real assembled pipeline without live network I/O — the Locked
    ///     pipeline-order regression coverage; see <see cref="BuildOpenAiV1Client" />'s <c>transportHttpClient</c>.
    ///     Internal plus <c>InternalsVisibleTo("XE-Local-AI-Engine.Tests")</c>, not part of the public contract.
    /// </remarks>
    internal OpenAIClient CreateOpenAiV1ClientForTesting(StoredAzureFoundryConnection connection, HttpClient transportHttpClient)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transportHttpClient);

        var endpoint = ResolveEndpoint(connection.Endpoint, connection.AdditionalAllowedHostSuffixes);
        return BuildOpenAiV1Client(endpoint, connection, transportHttpClient);
    }

    /// <summary>Builds the v1 key-credential client, setting the real key on the <c>api-key</c> header.</summary>
    /// <remarks>
    ///     The v1 surface validates a real <c>api-key</c> header, not the SDK's default <c>Authorization: Bearer</c>.
    ///     <c>ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy</c> is a stable (non-experimental) System.ClientModel factory that can target ANY header
    ///     name, so passing it as the ctor's <c>AuthenticationPolicy</c> sets the key on <c>api-key</c> with no prefix and writes nothing to
    ///     <c>Authorization</c>, leaving no placeholder value for a gateway to reject. It must be the ctor policy, never a PerCall one — see
    ///     <see cref="PlaceholderApiKey" /> and <see cref="EntraBearerTokenPipelinePolicy" />.
    /// </remarks>
#pragma warning disable OPENAI001 // OpenAIClient(AuthenticationPolicy, OpenAIClientOptions) is experimental.
    private static OpenAIClient BuildOpenAiV1KeyCredentialClient(string? apiKey, OpenAIClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An API key is required when the Azure Foundry connection uses API-key authentication.");
        }

        var authenticationPolicy = ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(new ApiKeyCredential(apiKey), "api-key");
        return new OpenAIClient(authenticationPolicy, options);
    }

    /// <summary>Builds the v1 Entra client, shared by both v1 Entra shapes: managed identity with a fixed scope, and EntraId with an operator-configured scope.</summary>
    /// <remarks>
    ///     Passed as the ctor's <c>AuthenticationPolicy</c>, not at <see cref="PipelinePosition.PerCall" />: a PerCall
    ///     registration on this surface is silently overwritten (see <see cref="EntraBearerTokenPipelinePolicy" />).
    /// </remarks>
    private static OpenAIClient BuildOpenAiV1EntraClient(TokenCredential credential, string scope, OpenAIClientOptions options)
    {
        var authenticationPolicy = new EntraBearerTokenPipelinePolicy(credential, scope);
        return new OpenAIClient(authenticationPolicy, options);
    }
#pragma warning restore OPENAI001

    private OpenAIClient BuildOpenAiV1EntraIdClient(StoredAzureFoundryConnection connection, OpenAIClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(connection.EntraTenantId)
            || string.IsNullOrWhiteSpace(connection.EntraClientId)
            || string.IsNullOrWhiteSpace(connection.EntraTokenScope))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An Entra ID connection requires a tenant id, client id, and token scope.");
        }

        var credential = BuildEntraCredential(connection);
        return BuildOpenAiV1EntraClient(credential, connection.EntraTokenScope, options);
    }

    /// <summary>Validates that the endpoint is absolute-HTTPS AND ends with a known Azure host suffix, before it is ever handed to the Azure client.</summary>
    /// <remarks>
    ///     The host allowlist matters most for managed identity: a <c>DefaultAzureCredential</c> Entra token must never
    ///     be sent to an arbitrary operator-entered host.
    /// </remarks>
    private static Uri ResolveEndpoint(string? endpoint, IReadOnlyList<string> extraAllowedHostSuffixes)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry endpoint must be an absolute HTTPS URL.");
        }

        if (!AzureFoundryEndpoints.IsAllowedHost(uri, extraAllowedHostSuffixes))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry endpoint host is not an allowed Azure host.");
        }

        return uri;
    }

    /// <summary>Attaches the custom-header policy at <see cref="PipelinePosition.PerCall" /> when the connection carries headers.</summary>
    /// <remarks>
    ///     Reserved names are skipped inside the policy and blank-name rows are dropped here.
    ///     <c>Diagnostics.IsLoggingContentEnabled</c> is left unset, so the SDK never logs a secret header value.
    /// </remarks>
    private static AzureOpenAIClientOptions BuildClientOptions(IReadOnlyList<StoredAzureFoundryHeader> headers)
    {
        var options = new AzureOpenAIClientOptions();
        AddCustomHeaderPolicy(options, headers);
        return options;
    }

    // Shared by both wire surfaces: AzureOpenAIClientOptions and OpenAIClientOptions both derive from the same
    // System.ClientModel ClientPipelineOptions base, so the one custom-header policy attaches identically to either.
    private static void AddCustomHeaderPolicy(ClientPipelineOptions options, IReadOnlyList<StoredAzureFoundryHeader> headers)
    {
        var resolved = ResolveHeaders(headers);
        if (resolved.Count > 0)
        {
            options.AddPolicy(new CustomHeaderPipelinePolicy(resolved), PipelinePosition.PerCall);
        }
    }

    private static IReadOnlyList<ResolvedCustomHeader> ResolveHeaders(IReadOnlyList<StoredAzureFoundryHeader> headers)
    {
        return
        [
            .. headers
               .Where(static header => !string.IsNullOrWhiteSpace(header.Name))
               .Select(static header => new ResolvedCustomHeader(header.Name.Trim(), header.Value ?? string.Empty))
        ];
    }

    private static AzureOpenAIClient BuildKeyCredentialClient(Uri endpoint, string? apiKey, AzureOpenAIClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An API key is required when the Azure Foundry connection uses API-key authentication.");
        }

        return new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey), options);
    }

    // Attaches the bearer-token policy for Entra ID auth and constructs the client with a
    // placeholder ApiKeyCredential — the real Authorization header is set per-call by the policy.
    private AzureOpenAIClient BuildEntraIdClient(Uri endpoint, StoredAzureFoundryConnection connection, AzureOpenAIClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(connection.EntraTenantId)
            || string.IsNullOrWhiteSpace(connection.EntraClientId)
            || string.IsNullOrWhiteSpace(connection.EntraTokenScope))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An Entra ID connection requires a tenant id, client id, and token scope.");
        }

        var credential = BuildEntraCredential(connection);
        options.AddPolicy(new EntraBearerTokenPipelinePolicy(credential, connection.EntraTokenScope), PipelinePosition.PerCall);

        return new AzureOpenAIClient(endpoint, new ApiKeyCredential(PlaceholderApiKey), options);
    }

    /// <summary>Selects the credential shape per the frozen contract.</summary>
    /// <remarks>
    ///     A secret with the AuthorizationCode sign-in method selects the delegated MSAL confidential-client flow ("Postman parity"): the secret
    ///     authenticates the code redemption but the resulting token is user-delegated, so the app-only <c>/.default</c> fail-fast does NOT apply to
    ///     that branch. A secret with any other sign-in method selects app-only client-credentials, with that fail-fast. No secret selects the
    ///     connection's chosen interactive sign-in method (device-code / browser).
    /// </remarks>
    private TokenCredential BuildEntraCredential(StoredAzureFoundryConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.EntraClientSecret))
        {
            if (connection.EntraSignInMethod == EntraSignInMethod.AuthorizationCode)
            {
                return BuildDelegatedAuthCodeCredential(connection);
            }

            ValidateClientCredentialsScope(connection.EntraTokenScope);
            return new ClientSecretCredential(connection.EntraTenantId, connection.EntraClientId, connection.EntraClientSecret);
        }

        return connection.EntraSignInMethod == EntraSignInMethod.InteractiveBrowser
            ? BuildInteractiveBrowserCredential(connection)
            : BuildSilentDeviceCodeCredential(connection);
    }

    /// <summary>Fails fast when the app-only client-credentials scope does not end in <see cref="ClientCredentialsScopeSuffix" />.</summary>
    /// <remarks>
    ///     A configured client secret with any sign-in method OTHER than AuthorizationCode selects the app-only
    ///     client-credentials flow (see <see cref="BuildEntraCredential" />), and Entra ID rejects that flow's token
    ///     request with AADSTS1002012 unless the scope ends in <c>/.default</c>. The scope round-trips to the UI and is
    ///     not secret, so echoing it in the error message is safe. <see cref="BuildEntraIdClient" /> already rejects a
    ///     null/blank scope before this runs; the null-conditional here is defense in depth, not the primary guard.
    /// </remarks>
    private static void ValidateClientCredentialsScope(string? tokenScope)
    {
        var trimmedScope = tokenScope?.Trim() ?? string.Empty;
        if (trimmedScope.EndsWith(ClientCredentialsScopeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
            "The Entra ID connection has a client secret, which uses the app-only client-credentials flow. That " +
            $"flow requires a token scope ending in '{ClientCredentialsScopeSuffix}' (e.g. " +
            $"api://<application-id-uri>{ClientCredentialsScopeSuffix}). The configured scope '{trimmedScope}' is " +
            "a delegated scope — either change the scope to the application's '/.default' scope, remove the " +
            "client secret and use device-code or browser sign-in for delegated access, or choose the " +
            "Authorization code sign-in method to use the secret with a delegated scope.");
    }

    /// <summary>Builds the delegated MSAL confidential-client credential, selected by a client secret plus the AuthorizationCode sign-in method.</summary>
    /// <remarks>
    ///     "Postman parity": the browser sign-in in Cloud Settings produced a delegated token (scp claim) while the
    ///     stored secret authenticated the code redemption. Like <see cref="BuildSilentDeviceCodeCredential" />, this
    ///     never prompts interactively from <c>Create()</c> — a missing persisted account surfaces as a typed
    ///     AuthRequired error.
    /// </remarks>
    private TokenCredential BuildDelegatedAuthCodeCredential(StoredAzureFoundryConnection connection)
    {
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        var liveCredential = _entraLiveCredentialCache?.TryGet(cacheKey);
        if (liveCredential is not null)
        {
            return liveCredential;
        }

        // Forced synchronous: every path below is reached from the synchronous IAzureFoundryChatClientFactory.Create, which RuntimeChatClient.ResolveActiveClient calls
        // from IChatClient.GetService — no async overload. Same for every pragma span in this file; see docs/wiki/16-code-conventions.md ("Blocking calls and cancellation forwarding").
#pragma warning disable MA0045, MA0032 // forced sync by IChatClient.GetService (see comment above)
        var homeAccountId = _entraAuthCodeAccountStore?.LoadHomeAccountIdAsync().GetAwaiter().GetResult();
#pragma warning restore MA0045, MA0032
        if (string.IsNullOrWhiteSpace(homeAccountId))
        {
            throw AuthCodeAuthRequired();
        }

        var redirectUri = EntraAuthCodeDefaults.ResolveRedirectUri(connection.EntraAuthCodeRedirectUri);
        var app = EntraAuthCodeConfidentialClientFactory.Build(connection.EntraTenantId!, connection.EntraClientId!, connection.EntraClientSecret!, redirectUri);

        if (_nodeDataDirectory is not null)
        {
#pragma warning disable MA0045, MA0032 // forced sync by IChatClient.GetService (see BuildDelegatedAuthCodeCredential)
            EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(app, _nodeDataDirectory, _logger).GetAwaiter().GetResult();
#pragma warning restore MA0045, MA0032
        }

        // GetAccountAsync(identifier) looks the account up directly by the persisted home-account-id, rather than
        // the obsolete GetAccountsAsync() + linear scan (MSAL guidance: better perf with a token-cache serializer).
#pragma warning disable MA0045, MA0032 // forced sync by IChatClient.GetService (see BuildDelegatedAuthCodeCredential)
        var account = app.GetAccountAsync(homeAccountId).GetAwaiter().GetResult();
#pragma warning restore MA0045, MA0032
        if (account is null)
        {
            throw AuthCodeAuthRequired();
        }

        var credential = new MsalDelegatedTokenCredential(app, account, connection.EntraTokenScope!);

        // Cache the rebuilt credential so the next send hits the live-cache fast path above instead of rebuilding
        // the confidential app + re-resolving the account on every single call after a process restart.
        _entraLiveCredentialCache?.Store(cacheKey, credential);

        return credential;
    }

    private static AzureFoundryProviderException AuthCodeAuthRequired()
    {
        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthRequired,
            "Authorization-code sign-in has not completed for this connection. Sign in via Cloud Settings first.");
    }

    /// <summary>Builds the device-code credential, which never prompts from inside <c>Create()</c>.</summary>
    /// <remarks>
    ///     A fresh interactive prompt mid-chat-send would hang headlessly, so a persisted <c>AuthenticationRecord</c>
    ///     from the separate device-code sign-in endpoint flow is required and its absence surfaces as a typed
    ///     AuthRequired error instead of blocking. A silent-refresh failure on an expired record is converted to the
    ///     same typed error by a callback that throws instead of falling back to a live device-code prompt.
    /// </remarks>
    private TokenCredential BuildSilentDeviceCodeCredential(StoredAzureFoundryConnection connection)
    {
        // Reuse the live, already-authenticated credential the sign-in coordinator cached on success: its MSAL token cache
        // (in-memory always, OS-native encrypted disk when available) holds the refresh token, unlike one rebuilt from the persisted record alone.
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        var liveCredential = _entraLiveCredentialCache?.TryGet(cacheKey);
        if (liveCredential is not null)
        {
            return liveCredential;
        }

        var record = LoadCachedAuthenticationRecord();
        if (record is null)
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthRequired,
                "Entra ID device-code sign-in has not completed for this connection. Sign in via Cloud Settings first.");
        }

        return new EntraPersistenceFallbackCredential(cacheOptions => new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = connection.EntraTenantId,
                ClientId = connection.EntraClientId,
                AuthenticationRecord = record,
                TokenCachePersistenceOptions = cacheOptions,
                DeviceCodeCallback = (_, _) => throw new CredentialUnavailableException("Entra ID silent authentication expired for this connection; sign in again via Cloud Settings.")
            }),
            new TokenCachePersistenceOptions
            {
                Name = EntraTokenCachePersistenceName
            },
            _logger);
    }

    /// <summary>Builds the interactive-browser credential, which IS allowed to prompt live from <c>Create()</c>.</summary>
    /// <remarks>
    ///     The browser opens on the node machine, which is correct for desktop mode. First use for a connection
    ///     authenticates eagerly, so a bad tenant/client id fails fast rather than deferring to the first chat send, and
    ///     persists the resulting record so a future restart resumes silently.
    /// </remarks>
    private TokenCredential BuildInteractiveBrowserCredential(StoredAzureFoundryConnection connection)
    {
        var record = LoadCachedAuthenticationRecord();
        if (record is not null)
        {
            return new InteractiveBrowserCredential(BuildInteractiveBrowserOptions(connection, record, allowPersistence: true));
        }

        // Single-flight gate: only one live browser prompt at a time. Without it every send arriving during an unfinished sign-in would miss the client cache, re-enter here
        // and open another browser window, each blocking its own send for the full timeout. Concurrent callers fail fast with a typed, retryable error instead of queuing.
        if (Interlocked.CompareExchange(ref _interactiveBrowserSignInInFlight, 1, 0) != 0)
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthRequired,
                "An interactive browser sign-in is already in progress. Complete it in the opened browser window, then send again.");
        }

        try
        {
            try
            {
                return AuthenticateInteractiveBrowser(connection, allowPersistence: true);
            }
            // A persistence failure does not always surface as CredentialUnavailableException: with no org.freedesktop.secrets provider it can
            // arrive as AuthenticationFailedException wrapping MsalCachePersistenceException levels deep (see EntraCachePersistenceFailure). Checking both is what makes the retry fire.
            catch (Exception exception) when (exception is CredentialUnavailableException || EntraCachePersistenceFailure.IsPersistenceUnavailable(exception))
            {
                _logger.LogWarning(exception,
                    "Encrypted Entra ID token-cache persistence is unavailable on this platform; retrying interactive sign-in with an in-memory (non-persisted) token cache.");
                return AuthenticateInteractiveBrowser(connection, allowPersistence: false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _interactiveBrowserSignInInFlight, 0);
        }
    }

    /// <summary>Authenticates a fresh interactive-browser sign-in, bounded by <see cref="InteractiveBrowserSignInTimeout" />.</summary>
    /// <remarks>
    ///     <c>Authenticate()</c> without a token waits forever on MSAL's localhost redirect listener when the operator closes the
    ///     browser without completing sign-in — the redirect never arrives — so the timeout token bounds that wait. Cancellation
    ///     surfaces either as <see cref="OperationCanceledException" /> directly or wrapped by Azure.Identity's diagnostic scope
    ///     in <see cref="AuthenticationFailedException" />, so both are translated to the same typed, retryable error. The
    ///     persistence-unavailable retry in <see cref="BuildInteractiveBrowserCredential" /> is NOT affected: its exception filter matches neither translation.
    /// </remarks>
    private InteractiveBrowserCredential AuthenticateInteractiveBrowser(StoredAzureFoundryConnection connection, bool allowPersistence)
    {
        var credential = new InteractiveBrowserCredential(BuildInteractiveBrowserOptions(connection, record: null, allowPersistence));

        using var timeout = new CancellationTokenSource(InteractiveBrowserSignInTimeout);
        AuthenticationRecord record;
        try
        {
#pragma warning disable MA0045 // forced sync by IChatClient.GetService (see BuildDelegatedAuthCodeCredential)
            record = credential.Authenticate(timeout.Token);
#pragma warning restore MA0045
        }
        catch (OperationCanceledException exception)
        {
            throw InteractiveSignInIncomplete(exception);
        }
        catch (AuthenticationFailedException exception) when (timeout.IsCancellationRequested)
        {
            throw InteractiveSignInIncomplete(exception);
        }

        PersistAuthenticationRecord(record);
        return credential;
    }

    /// <summary>Test-only seam that marks the interactive-browser single-flight gate as held.</summary>
    /// <remarks>
    ///     The concurrent-caller fail-fast path can then be exercised without a live browser prompt blocking the first
    ///     caller. Internal plus <c>InternalsVisibleTo</c>, mirroring <see cref="CreateOpenAiV1ClientForTesting" />; not
    ///     part of the public contract.
    /// </remarks>
    internal void MarkInteractiveBrowserSignInInFlightForTesting()
    {
        Interlocked.Exchange(ref _interactiveBrowserSignInInFlight, 1);
    }

    private static AzureFoundryProviderException InteractiveSignInIncomplete(Exception exception)
    {
        return new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthRequired,
            $"Interactive browser sign-in was not completed within {InteractiveBrowserSignInTimeout.TotalMinutes:0} " +
            "minutes (the browser window was closed or the prompt was abandoned). Send again to retry the sign-in.",
            exception);
    }

    private static InteractiveBrowserCredentialOptions BuildInteractiveBrowserOptions(StoredAzureFoundryConnection connection,
        AuthenticationRecord? record,
        bool allowPersistence)
    {
        return new InteractiveBrowserCredentialOptions
        {
            TenantId = connection.EntraTenantId,
            ClientId = connection.EntraClientId,
            AuthenticationRecord = record,
            TokenCachePersistenceOptions = allowPersistence
                ? new TokenCachePersistenceOptions
                {
                    Name = EntraTokenCachePersistenceName
                }
                : null
        };
    }

    private AuthenticationRecord? LoadCachedAuthenticationRecord()
    {
#pragma warning disable MA0045, MA0032 // forced sync by IChatClient.GetService (see BuildDelegatedAuthCodeCredential)
        return _entraTokenCacheStore?.LoadRecordAsync().GetAwaiter().GetResult();
#pragma warning restore MA0045, MA0032
    }

    private void PersistAuthenticationRecord(AuthenticationRecord record)
    {
        if (_entraTokenCacheStore is null)
        {
            return;
        }

        try
        {
#pragma warning disable MA0045, MA0032 // forced sync by IChatClient.GetService (see BuildDelegatedAuthCodeCredential)
            _entraTokenCacheStore.SaveRecordAsync(record).GetAwaiter().GetResult();
#pragma warning restore MA0045, MA0032
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Failed to persist the Entra ID authentication record.");
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Failed to persist the Entra ID authentication record.");
        }
    }
}
