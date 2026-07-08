namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class AzureFoundryChatClientFactoryTests
{
    [Test]
    public void Create_WhenConnectionIsValid_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateConnection(), "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEndpointIsBlank_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(endpoint: " "), "gpt-4o"));
    }

    [Test]
    public void Create_WhenApiKeyModeMissingKey_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(apiKey: " "), "gpt-4o"));
    }

    [Test]
    public void Create_WhenDeploymentNameIsBlank_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(), " "));
    }

    [Test]
    public void Create_WhenHostNotAllowlisted_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(endpoint: "https://evil.example.com/"), "gpt-4o"));
    }

    [Test]
    public void Create_WhenManagedIdentityMode_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
            ApiKey = null,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenHeadersPresent_ApiKey_BuildsClient()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = CreateConnection() with
        {
            Headers =
            [
                new StoredAzureFoundryHeader
                {
                    Name = "Ocp-Apim-Subscription-Key",
                    Value = "sub",
                    IsSecret = true
                }
            ]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenHeadersPresent_ManagedIdentity_BuildsClient()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.services.ai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ],
            Headers =
            [
                new StoredAzureFoundryHeader
                {
                    Name = "X-Tenant",
                    Value = "tenant-a",
                    IsSecret = false
                }
            ]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_ManagedIdentity_CustomHost_AllowedWhenSuffixPresent()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://gateway.azure-api.net/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ],
            AdditionalAllowedHostSuffixes = [".azure-api.net"]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_CustomHost_WithoutSuffix_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(endpoint: "https://gateway.azure-api.net/"), "gpt-4o"));
    }

    [Test]
    public void Create_WhenEntraIdClientSecretMode_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateEntraConnection(clientSecret: "test-client-secret"), "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEntraIdMissingTenantId_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateEntraConnection(clientSecret: "secret") with
        {
            EntraTenantId = " "
        }, "gpt-4o"));
    }

    [Test]
    public void Create_WhenEntraIdMissingTokenScope_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateEntraConnection(clientSecret: "secret") with
        {
            EntraTokenScope = " "
        }, "gpt-4o"));
    }

    [Test]
    public void Create_WhenEntraIdDeviceCodeWithNoCachedRecord_ThrowsAuthRequired()
    {
        var factory = new AzureFoundryChatClientFactory(new FakeEntraTokenCacheStore(record: null));

        try
        {
            factory.Create(CreateEntraConnection(signInMethod: EntraSignInMethod.DeviceCode), "gpt-4o");
        }
        catch (AzureFoundryProviderException exception)
        {
            AssertEx.True(exception.Kind == AzureFoundryProviderErrorKind.AuthRequired);
            return;
        }

        throw new AssertionException($"Expected {nameof(AzureFoundryProviderException)} of kind {nameof(AzureFoundryProviderErrorKind.AuthRequired)}.");
    }

    [Test]
    public void Create_WhenEntraIdDeviceCodeWithCachedRecord_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory(new FakeEntraTokenCacheStore(CreateAuthenticationRecord()));

        var chatClient = factory.Create(CreateEntraConnection(signInMethod: EntraSignInMethod.DeviceCode), "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEntraIdDeviceCodeWithLiveCachedCredential_ReturnsChatClientAdapter_EvenWithoutPersistedRecord()
    {
        // Regression guard: on a platform where OS-native encrypted token-cache persistence is unavailable, the
        // persisted AuthenticationRecord alone has nothing to silently refresh from. The sign-in coordinator's live
        // credential cache is the fix — a matching cache entry must let Create() succeed even when the persisted
        // record store is empty (proving the factory does not depend solely on IEntraTokenCacheStore).
        var connection = CreateEntraConnection(signInMethod: EntraSignInMethod.DeviceCode);
        var liveCredentialCache = new EntraLiveCredentialCache();
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        liveCredentialCache.Store(cacheKey, new StubTokenCredential());
        var factory = new AzureFoundryChatClientFactory(new FakeEntraTokenCacheStore(record: null), liveCredentialCache);

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEntraIdDeviceCodeWithLiveCachedCredentialUnderADifferentKey_ThrowsAuthRequired()
    {
        // The live-cache hit must be key-scoped: a cached credential for a DIFFERENT tenant/client/scope must never
        // be handed out for this connection.
        var liveCredentialCache = new EntraLiveCredentialCache();
        liveCredentialCache.Store(EntraDeviceCodeCredentialCacheKey.Create("other-tenant", "other-client", "other-scope"),
            new StubTokenCredential());
        var factory = new AzureFoundryChatClientFactory(new FakeEntraTokenCacheStore(record: null), liveCredentialCache);

        try
        {
            factory.Create(CreateEntraConnection(signInMethod: EntraSignInMethod.DeviceCode), "gpt-4o");
        }
        catch (AzureFoundryProviderException exception)
        {
            AssertEx.True(exception.Kind == AzureFoundryProviderErrorKind.AuthRequired);
            return;
        }

        throw new AssertionException($"Expected {nameof(AzureFoundryProviderException)} of kind {nameof(AzureFoundryProviderErrorKind.AuthRequired)}.");
    }

    [Test]
    public void Create_WhenEntraIdClientSecretWithDelegatedScope_ThrowsConfigError_WithoutSecret()
    {
        var factory = new AzureFoundryChatClientFactory();
        const string secret = "super-secret-value";

        try
        {
            factory.Create(CreateEntraConnection(clientSecret: secret) with
            {
                EntraTokenScope = "api://backend-app/access_as_user"
            }, "gpt-4o");
        }
        catch (AzureFoundryProviderException exception)
        {
            AssertEx.True(exception.Kind == AzureFoundryProviderErrorKind.Configuration);
            AssertEx.True(exception.Message.Contains("/.default", StringComparison.Ordinal));
            AssertEx.False(exception.Message.Contains(secret, StringComparison.Ordinal));
            return;
        }

        throw new AssertionException($"Expected {nameof(AzureFoundryProviderException)} of kind {nameof(AzureFoundryProviderErrorKind.Configuration)}.");
    }

    [Test]
    public void Create_WhenEntraIdClientSecretWithDefaultScope_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateEntraConnection(clientSecret: "secret") with
        {
            EntraTokenScope = "api://backend-app/.default"
        }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEntraIdClientSecretWithDefaultScope_UppercaseSuffix_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateEntraConnection(clientSecret: "secret") with
        {
            EntraTokenScope = "api://backend-app/.DEFAULT"
        }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenEntraIdWithHeaders_BuildsClient()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = CreateEntraConnection(clientSecret: "secret") with
        {
            Headers =
            [
                new StoredAzureFoundryHeader
                {
                    Name = "X-Tenant",
                    Value = "tenant-a",
                    IsSecret = false
                }
            ]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenOpenAiV1ApiKeyMode_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateConnection() with { ApiSurface = AzureFoundryApiSurface.OpenAiV1 }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenOpenAiV1ApiKeyModeMissingKey_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(CreateConnection(apiKey: " ") with { ApiSurface = AzureFoundryApiSurface.OpenAiV1 }, "gpt-4o"));
    }

    [Test]
    public void Create_WhenOpenAiV1ManagedIdentityMode_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ManagedIdentity,
            ApiSurface = AzureFoundryApiSurface.OpenAiV1,
            ApiKey = null,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenOpenAiV1EntraIdClientSecretWithDefaultScope_ReturnsChatClientAdapter()
    {
        var factory = new AzureFoundryChatClientFactory();

        var chatClient = factory.Create(CreateEntraConnection(clientSecret: "secret") with
        {
            ApiSurface = AzureFoundryApiSurface.OpenAiV1,
            EntraTokenScope = "api://backend-app/.default"
        }, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenOpenAiV1EntraIdClientSecretWithDelegatedScope_ThrowsConfigError_WithoutSecret()
    {
        var factory = new AzureFoundryChatClientFactory();
        const string secret = "super-secret-value";

        try
        {
            factory.Create(CreateEntraConnection(clientSecret: secret) with
            {
                ApiSurface = AzureFoundryApiSurface.OpenAiV1,
                EntraTokenScope = "api://backend-app/access_as_user"
            }, "gpt-4o");
        }
        catch (AzureFoundryProviderException exception)
        {
            AssertEx.True(exception.Kind == AzureFoundryProviderErrorKind.Configuration);
            AssertEx.True(exception.Message.Contains("/.default", StringComparison.Ordinal));
            AssertEx.False(exception.Message.Contains(secret, StringComparison.Ordinal));
            return;
        }

        throw new AssertionException($"Expected {nameof(AzureFoundryProviderException)} of kind {nameof(AzureFoundryProviderErrorKind.Configuration)}.");
    }

    [Test]
    public void Create_WhenOpenAiV1WithHeaders_BuildsClient()
    {
        var factory = new AzureFoundryChatClientFactory();
        var connection = CreateConnection() with
        {
            ApiSurface = AzureFoundryApiSurface.OpenAiV1,
            Headers =
            [
                new StoredAzureFoundryHeader
                {
                    Name = "X-Tenant",
                    Value = "tenant-a",
                    IsSecret = false
                }
            ]
        };

        var chatClient = factory.Create(connection, "gpt-4o");

        AssertEx.NotNull(chatClient);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public void Create_WhenOpenAiV1HostNotAllowlisted_ThrowsConfigError()
    {
        var factory = new AzureFoundryChatClientFactory();

        ThrowsConfig(() => factory.Create(
            CreateConnection(endpoint: "https://evil.example.com/") with { ApiSurface = AzureFoundryApiSurface.OpenAiV1 },
            "gpt-4o"));
    }

    private static void ThrowsConfig(Action action)
    {
        try
        {
            action();
        }
        catch (AzureFoundryProviderException exception)
        {
            AssertEx.True(exception.Kind == AzureFoundryProviderErrorKind.Configuration);
            return;
        }

        throw new AssertionException($"Expected {nameof(AzureFoundryProviderException)} of kind {nameof(AzureFoundryProviderErrorKind.Configuration)}.");
    }

    private static StoredAzureFoundryConnection CreateEntraConnection(string endpoint = "https://example.openai.azure.com/",
        string? clientSecret = null,
        EntraSignInMethod signInMethod = EntraSignInMethod.ClientSecret)
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = endpoint,
            AuthMode = AzureFoundryAuthMode.EntraId,
            EntraTenantId = "tenant-id",
            EntraClientId = "client-id",
            EntraClientSecret = clientSecret,
            EntraTokenScope = "api://backend-app/.default",
            EntraSignInMethod = signInMethod,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        };
    }

    // AuthenticationRecord has no public constructor (Azure.Identity builds it internally from a live sign-in or
    // MSAL AuthenticationResult) — deserializing a hand-crafted payload matching its own documented Serialize()
    // schema is the supported way to obtain one for tests without a live interactive/device-code sign-in.
    private static AuthenticationRecord CreateAuthenticationRecord()
    {
        const string json = """
                            {"username":"user@contoso.com","authority":"https://login.microsoftonline.com/tenant-id","homeAccountId":"home-account-id","tenantId":"tenant-id","clientId":"client-id","version":"1.0"}
                            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return AuthenticationRecord.Deserialize(stream);
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken("stub-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class FakeEntraTokenCacheStore(AuthenticationRecord? record) : IEntraTokenCacheStore
    {
        public Task<AuthenticationRecord?> LoadRecordAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(record);
        }

        public Task SaveRecordAsync(AuthenticationRecord record, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static StoredAzureFoundryConnection CreateConnection(string endpoint = "https://example.openai.azure.com/",
        string? apiKey = "test-api-key")
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = endpoint,
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiKey = apiKey,
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        };
    }
}
