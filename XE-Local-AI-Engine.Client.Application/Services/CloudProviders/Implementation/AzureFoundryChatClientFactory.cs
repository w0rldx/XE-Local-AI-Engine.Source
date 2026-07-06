namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

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
    // Placeholder key material: the AzureOpenAIClient ctor requires an ApiKeyCredential even when auth is Entra ID.
    // The value is never sent as the real bearer — EntraBearerTokenPipelinePolicy overwrites Authorization on every
    // call — and the SDK's own "api-key" header carrying this placeholder is harmless noise to an APIM gateway that
    // validates the bearer token instead.
    private const string EntraPlaceholderApiKey = "unused-entra-id-auth";
    private const string EntraTokenCachePersistenceName = "XE-Local-AI-Engine.Client.AzureFoundry.EntraId";

    private readonly IEntraTokenCacheStore? _entraTokenCacheStore;
    private readonly ILogger<AzureFoundryChatClientFactory> _logger;

    public AzureFoundryChatClientFactory(IEntraTokenCacheStore? entraTokenCacheStore = null,
        ILogger<AzureFoundryChatClientFactory>? logger = null)
    {
        _entraTokenCacheStore = entraTokenCacheStore;
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

        var options = BuildClientOptions(connection.Headers);

        var azureClient = connection.AuthMode switch
        {
            AzureFoundryAuthMode.ApiKey => BuildKeyCredentialClient(endpoint, connection.ApiKey, options),
            AzureFoundryAuthMode.ManagedIdentity => new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), options),
            AzureFoundryAuthMode.EntraId => BuildEntraIdClient(endpoint, connection, options),
            _ => throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry connection has an unsupported authentication mode.")
        };

        var innerClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
        return new AzureFoundryErrorTranslatingChatClient(innerClient);
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

        var resolved = ResolveHeaders(headers);
        if (resolved.Count > 0)
        {
            options.AddPolicy(new CustomHeaderPipelinePolicy(resolved), PipelinePosition.PerCall);
        }

        return options;
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

        return new AzureOpenAIClient(endpoint, new ApiKeyCredential(EntraPlaceholderApiKey), options);
    }

    // Selects the credential shape per the frozen contract: a configured client secret always wins (app-only
    // client-credentials); otherwise the connection's chosen interactive sign-in method applies.
    private TokenCredential BuildEntraCredential(StoredAzureFoundryConnection connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.EntraClientSecret))
        {
            return new ClientSecretCredential(connection.EntraTenantId, connection.EntraClientId, connection.EntraClientSecret);
        }

        return connection.EntraSignInMethod == EntraSignInMethod.InteractiveBrowser
            ? BuildInteractiveBrowserCredential(connection)
            : BuildSilentDeviceCodeCredential(connection);
    }

    // Device-code mode never prompts from inside Create() because a fresh interactive prompt mid-chat-send would
    // hang headlessly. A persisted AuthenticationRecord from the separate device-code sign-in endpoint flow is
    // required, and its absence surfaces as a typed AuthRequired error instead of blocking. Silent-refresh failure
    // on an expired record is converted to the same typed error via a callback that throws instead of falling back
    // to a live device-code prompt.
    private TokenCredential BuildSilentDeviceCodeCredential(StoredAzureFoundryConnection connection)
    {
        var record = LoadCachedAuthenticationRecord();
        if (record is null)
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.AuthRequired,
                "Entra ID device-code sign-in has not completed for this connection. Sign in via Cloud Settings first.");
        }

        return new EntraPersistenceFallbackCredential(
            cacheOptions => new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = connection.EntraTenantId,
                ClientId = connection.EntraClientId,
                AuthenticationRecord = record,
                TokenCachePersistenceOptions = cacheOptions,
                DeviceCodeCallback = (_, _) => throw new CredentialUnavailableException(
                    "Entra ID silent authentication expired for this connection; sign in again via Cloud Settings.")
            }),
            new TokenCachePersistenceOptions { Name = EntraTokenCachePersistenceName },
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
        catch (CredentialUnavailableException exception)
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
            TokenCachePersistenceOptions = allowPersistence ? new TokenCachePersistenceOptions { Name = EntraTokenCachePersistenceName } : null
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
