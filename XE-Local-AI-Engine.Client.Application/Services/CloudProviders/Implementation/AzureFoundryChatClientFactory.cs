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
    // Placeholder key material for the Azure-deployments surface ONLY (AzureOpenAIClient's ctor requires an
    // ApiKeyCredential even when the real auth is Entra ID). EntraBearerTokenPipelinePolicy is registered at
    // PipelinePosition.PerCall on that surface and overwrites Authorization on every call, so the SDK's own
    // Authorization header carrying this placeholder is harmless noise.
    //
    // The v1 surface (plain OpenAIClient) does NOT use this placeholder for either auth mode: its SDK-internal
    // authentication policy runs in a FIXED pipeline slot that sits AFTER every PerCall policy (see
    // EntraBearerTokenPipelinePolicy's remarks), so a placeholder credential there would silently overwrite whatever
    // a PerCall policy set — that was the root cause of a live gateway rejecting Entra ID sends with "JWT must have
    // three segments" (the placeholder string, not a real token, reached the wire). The v1 builders below instead
    // construct the OpenAIClient with a real AuthenticationPolicy directly (OpenAIClient(AuthenticationPolicy,
    // OpenAIClientOptions), OPENAI001-experimental) so there is no placeholder in that FIXED slot to begin with.
    private const string PlaceholderApiKey = "unused-entra-id-auth";
    private const string EntraTokenCachePersistenceName = "XE-Local-AI-Engine.Client.AzureFoundry.EntraId";

    // The v1 surface path segment appended to the connection endpoint (Locked v1 surface contract: no api-version
    // query param, trailing slash so the OpenAI SDK's own relative-path joining lands on .../openai/v1/chat/completions).
    private const string OpenAiV1PathSegment = "/openai/v1/";

    // Documented Entra ID scope for the v1 surface under managed-identity auth (Microsoft Learn, 2026-06). The
    // ApiKey/EntraId auth modes carry their own scope (the API key itself, or the operator-configured
    // EntraTokenScope) so this constant applies to ManagedIdentity only.
    private const string ManagedIdentityV1Scope = "https://ai.azure.com/.default";

    // Client-credentials (app-only) token requests are rejected by Entra ID (AADSTS1002012) unless the scope ends
    // in "/.default" — a delegated scope like "api://<app-id-uri>/access_as_user" only works with a user-delegated
    // flow (device-code / interactive browser). The scope is intentionally never auto-rewritten: there is no safe
    // general rule for a multi-segment App-ID-URI or a trailing-slash host, so a mismatch fails fast instead.
    private const string ClientCredentialsScopeSuffix = "/.default";

    private readonly IEntraAuthCodeAccountStore? _entraAuthCodeAccountStore;
    private readonly IEntraLiveCredentialCache? _entraLiveCredentialCache;
    private readonly IEntraTokenCacheStore? _entraTokenCacheStore;
    private readonly ILogger<AzureFoundryChatClientFactory> _logger;
    private readonly INodeDataDirectory? _nodeDataDirectory;

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

    // The OpenAI-compatible v1 surface (ApiSurface.OpenAiV1): {endpoint}/openai/v1/chat/completions, deployment name
    // in the request body's "model" field. Built via the plain OpenAI SDK client — see BuildOpenAiV1KeyCredentialClient
    // / BuildOpenAiV1EntraClient for why the same ResolveEndpoint validation and credential shapes as the Azure
    // deployments surface still apply, just wired through OpenAIClientOptions instead of AzureOpenAIClientOptions.
    //
    // transportHttpClient is a test-only seam (default null in the production Create() path): it lets
    // CreateOpenAiV1ClientForTesting point the assembled client's transport at a capturing fake HttpClient instead of
    // the network, so a pipeline-EXECUTION test can assert on the actual outbound request headers/URI rather than
    // just the wiring code that produced them — the class of bug this factory shipped (see PlaceholderApiKey's
    // remarks) was invisible to construction-only tests.
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

    // Test-only seam (Locked pipeline-order regression coverage, see BuildOpenAiV1Client's transportHttpClient
    // remarks): assembles the SAME v1 client construction path Create() uses, with an injected transport so a
    // request can be fired through the real assembled pipeline without live network I/O. Internal +
    // InternalsVisibleTo("XE-Local-AI-Engine.Tests") — not part of the public contract.
    internal OpenAIClient CreateOpenAiV1ClientForTesting(StoredAzureFoundryConnection connection, HttpClient transportHttpClient)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transportHttpClient);

        var endpoint = ResolveEndpoint(connection.Endpoint, connection.AdditionalAllowedHostSuffixes);
        return BuildOpenAiV1Client(endpoint, connection, transportHttpClient);
    }

    // The v1 surface validates a real "api-key" header, not the SDK's default "Authorization: Bearer <key>".
    // ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy is a stable (non-experimental) System.ClientModel factory
    // that can target ANY header name, not just Authorization — passing it directly as the ctor's AuthenticationPolicy
    // sets the real key on "api-key" with no prefix and writes nothing to Authorization at all, so there is no
    // placeholder value left for a gateway to reject (mirrors the Entra fix: see PlaceholderApiKey's remarks and
    // EntraBearerTokenPipelinePolicy's FIXED-slot documentation for why this must be the ctor policy, not a PerCall one).
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

    // Shared by both v1 Entra shapes (managed identity with a fixed scope, and EntraId with an operator-configured
    // scope). Passed as the ctor's AuthenticationPolicy (not PipelinePosition.PerCall) — see
    // EntraBearerTokenPipelinePolicy's remarks for why PerCall registration on this surface is silently overwritten.
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

    // Validates the endpoint is absolute-HTTPS AND ends with a known Azure host suffix before it is ever handed to the
    // Azure client. The host allowlist matters most for managed identity: a DefaultAzureCredential Entra token must
    // never be sent to an arbitrary operator-entered host (MEDIUM-4).
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

    // Attaches the custom-header policy at PerCall when the connection carries headers (Locked #4). Reserved names are
    // skipped inside the policy; blank-name rows are dropped here. Diagnostics.IsLoggingContentEnabled is left unset
    // so secret header values are never logged by the SDK (security LOW-6).
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

    private static IReadOnlyList<(string Name, string Value)> ResolveHeaders(IReadOnlyList<StoredAzureFoundryHeader> headers)
    {
        return
        [
            .. headers
               .Where(static header => !string.IsNullOrWhiteSpace(header.Name))
               .Select(static header => (header.Name.Trim(), header.Value ?? string.Empty))
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

    // Attaches the bearer-token policy for Entra ID auth (Locked build contract §8) and constructs the client with a
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

    // Selects the credential shape per the frozen contract:
    //  - secret + AuthorizationCode sign-in method -> delegated MSAL confidential-client flow (Postman parity): the
    //    secret authenticates the code redemption, but the resulting token is user-delegated, so the app-only
    //    /.default fail-fast below does NOT apply to this branch.
    //  - secret + any other sign-in method -> app-only client-credentials (existing behavior + fail-fast).
    //  - no secret -> the connection's chosen interactive sign-in method (device-code / browser), unchanged.
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

    // A configured client secret with any sign-in method OTHER than AuthorizationCode selects the app-only
    // client-credentials flow (see BuildEntraCredential), and Entra ID rejects that flow's token request with
    // AADSTS1002012 unless the scope ends in "/.default". The scope round-trips to the UI and is not secret, so it
    // is safe to echo back in the error message. The caller (BuildEntraIdClient) already rejects a null/blank scope
    // before this runs; the null-conditional here is defense in depth, not the primary guard.
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

    // Delegated MSAL confidential-client flow selected by a client secret + AuthorizationCode sign-in method
    // (Postman parity): the browser sign-in in Cloud Settings produced a delegated token (scp claim) while the
    // stored secret authenticated the code redemption. Like BuildSilentDeviceCodeCredential, this never prompts
    // interactively from Create() — a missing persisted account surfaces as a typed AuthRequired error.
    private TokenCredential BuildDelegatedAuthCodeCredential(StoredAzureFoundryConnection connection)
    {
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        var liveCredential = _entraLiveCredentialCache?.TryGet(cacheKey);
        if (liveCredential is not null)
        {
            return liveCredential;
        }

        var homeAccountId = _entraAuthCodeAccountStore?.LoadHomeAccountIdAsync().GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(homeAccountId))
        {
            throw AuthCodeAuthRequired();
        }

        var redirectUri = EntraAuthCodeDefaults.ResolveRedirectUri(connection.EntraAuthCodeRedirectUri);
        var app = EntraAuthCodeConfidentialClientFactory.Build(connection.EntraTenantId!, connection.EntraClientId!, connection.EntraClientSecret!, redirectUri);

        if (_nodeDataDirectory is not null)
        {
            EntraAuthCodeConfidentialClientFactory.TryRegisterPersistentCacheAsync(app, _nodeDataDirectory, _logger).GetAwaiter().GetResult();
        }

        // GetAccountAsync(identifier) looks the account up directly by the persisted home-account-id, rather than
        // the obsolete GetAccountsAsync() + linear scan (MSAL guidance: better perf with a token-cache serializer).
        var account = app.GetAccountAsync(homeAccountId).GetAwaiter().GetResult();
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

    // Device-code mode never prompts from inside Create() because a fresh interactive prompt mid-chat-send would
    // hang headlessly. A persisted AuthenticationRecord from the separate device-code sign-in endpoint flow is
    // required, and its absence surfaces as a typed AuthRequired error instead of blocking. Silent-refresh failure
    // on an expired record is converted to the same typed error via a callback that throws instead of falling back
    // to a live device-code prompt.
    private TokenCredential BuildSilentDeviceCodeCredential(StoredAzureFoundryConnection connection)
    {
        // Reuse the live, already-authenticated credential the sign-in coordinator cached on success: its MSAL
        // token cache (in-memory always, plus OS-native encrypted disk when available) is what actually holds the
        // refresh token, whereas a credential rebuilt from only the persisted record has nothing to silently
        // refresh from when encrypted persistence was unavailable on this platform (Locked build contract §9).
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

    // Interactive-browser mode is allowed to prompt live from Create() (Locked decision #2 / §9: the browser opens
    // on the node machine, which is correct for desktop mode). First use for a connection authenticates eagerly so a
    // bad tenant/client id fails fast rather than deferring to the first chat send, and persists the resulting
    // record so a future restart resumes silently.
    private TokenCredential BuildInteractiveBrowserCredential(StoredAzureFoundryConnection connection)
    {
        var record = LoadCachedAuthenticationRecord();
        if (record is not null)
        {
            return new InteractiveBrowserCredential(BuildInteractiveBrowserOptions(connection, record, allowPersistence: true));
        }

        try
        {
            return AuthenticateInteractiveBrowser(connection, allowPersistence: true);
        }
        // A persistence failure does not always surface as CredentialUnavailableException — on a platform with no
        // org.freedesktop.secrets provider (e.g. WSL2 without gnome-keyring/kwallet) it can arrive as
        // AuthenticationFailedException wrapping MsalCachePersistenceException several levels deep instead (see
        // EntraCachePersistenceFailure's remarks). Checking both is what makes the retry actually fire instead of
        // the sign-in failing outright.
        catch (Exception exception) when (exception is CredentialUnavailableException || EntraCachePersistenceFailure.IsPersistenceUnavailable(exception))
        {
            _logger.LogWarning(exception,
                "Encrypted Entra ID token-cache persistence is unavailable on this platform; retrying interactive sign-in with an in-memory (non-persisted) token cache.");
            return AuthenticateInteractiveBrowser(connection, allowPersistence: false);
        }
    }

    private InteractiveBrowserCredential AuthenticateInteractiveBrowser(StoredAzureFoundryConnection connection, bool allowPersistence)
    {
        var credential = new InteractiveBrowserCredential(BuildInteractiveBrowserOptions(connection, record: null, allowPersistence));
        var record = credential.Authenticate();
        PersistAuthenticationRecord(record);
        return credential;
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
        return _entraTokenCacheStore?.LoadRecordAsync().GetAwaiter().GetResult();
    }

    private void PersistAuthenticationRecord(AuthenticationRecord record)
    {
        if (_entraTokenCacheStore is null)
        {
            return;
        }

        try
        {
            _entraTokenCacheStore.SaveRecordAsync(record).GetAwaiter().GetResult();
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
