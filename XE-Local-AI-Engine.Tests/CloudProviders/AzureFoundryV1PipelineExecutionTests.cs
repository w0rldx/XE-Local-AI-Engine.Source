namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.ClientModel;
using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pipeline-EXECUTION regression coverage for the v1-surface auth-policy ordering, which a real APIM gateway
///     enforces: <see cref="OpenAI.OpenAIClient" />'s ctor builds its own authentication policy from a
///     placeholder <see cref="System.ClientModel.ApiKeyCredential" /> and places it in a FIXED pipeline slot that the
///     SDK's internal pipeline-assembly code puts AFTER every <c>PipelinePosition.PerCall</c> policy — so a real
///     Entra bearer token set by a PerCall <see cref="EntraBearerTokenPipelinePolicy" /> can be silently overwritten by
///     the placeholder before the request leaves the process. Construction-only tests (does <c>Create()</c> return a
///     client?) cannot catch this: it only shows up in the actual bytes on the wire. These tests fire ONE
///     request through the REAL assembled <see cref="OpenAI.OpenAIClient" /> pipeline — the same construction path
///     <see cref="AzureFoundryChatClientFactory.Create" /> uses, via the <see cref="AzureFoundryChatClientFactory.CreateOpenAiV1ClientForTesting" />
///     test seam — over a request-capturing fake transport, and assert on the CAPTURED outbound request.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AzureFoundryV1PipelineExecutionTests
{
    // header.payload.signature — three segments, matching the JWT shape a gateway's validate-jwt policy expects.
    // Never a real token: fixed test fixture only.
    private const string FakeJwt = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJmYWtlLXRva2VuIn0.ZmFrZS1zaWduYXR1cmU";

    [Test]
    public async Task EntraId_SendsRealBearerToken_NotPlaceholder()
    {
        var connection = CreateEntraConnection();
        var liveCredentialCache = new EntraLiveCredentialCache();
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        liveCredentialCache.Store(cacheKey, new StubTokenCredential(FakeJwt));
        var factory = new AzureFoundryChatClientFactory(entraLiveCredentialCache: liveCredentialCache);
        using var capture = new RequestCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var openAiClient = factory.CreateOpenAiV1ClientForTesting(connection, httpClient);
        await SendCannedChatRequestAsync(openAiClient);

        var request = AssertEx.NotNull(capture.LastRequest, "the chat request must have reached the fake transport");
        AssertEx.True(request.Headers.TryGetValues("Authorization", out var authValues), "Authorization header must be present");
        AssertEx.Equal($"Bearer {FakeJwt}", authValues!.Single());
        AssertEx.Equal(new Uri("https://example.openai.azure.com/openai/v1/chat/completions"), request.RequestUri);
    }

    [Test]
    public async Task EntraId_AuthorizationCodeMode_SendsRealDelegatedBearerToken_NotPlaceholder()
    {
        // Proves the new AuthorizationCode branch (client secret + EntraSignInMethod.AuthorizationCode -> delegated
        // MSAL credential, see AzureFoundryChatClientFactory.BuildEntraCredential's truth table) feeds the SAME
        // EntraBearerTokenPipelinePolicy as every other Entra ID shape — a live-cache hit is enough; the coordinator
        // and MSAL redemption itself are exercised separately (see EntraAuthCodeSignInCoordinatorTests's remarks).
        var connection = CreateEntraConnection() with
        {
            EntraClientSecret = "client-secret",
            EntraSignInMethod = EntraSignInMethod.AuthorizationCode
        };
        var liveCredentialCache = new EntraLiveCredentialCache();
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        liveCredentialCache.Store(cacheKey, new StubTokenCredential(FakeJwt));
        var factory = new AzureFoundryChatClientFactory(entraLiveCredentialCache: liveCredentialCache);
        using var capture = new RequestCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var openAiClient = factory.CreateOpenAiV1ClientForTesting(connection, httpClient);
        await SendCannedChatRequestAsync(openAiClient);

        var request = AssertEx.NotNull(capture.LastRequest, "the chat request must have reached the fake transport");
        AssertEx.True(request.Headers.TryGetValues("Authorization", out var authValues), "Authorization header must be present");
        AssertEx.Equal($"Bearer {FakeJwt}", authValues!.Single());
    }

    [Test]
    public async Task EntraId_ComposesWithCustomHeaders_WithoutEitherClobberingTheOther()
    {
        var connection = CreateEntraConnection(headers:
        [
            new StoredAzureFoundryHeader
            {
                Name = "X-Tenant",
                Value = "tenant-a",
                IsSecret = false
            }
        ]);
        var liveCredentialCache = new EntraLiveCredentialCache();
        var cacheKey = EntraDeviceCodeCredentialCacheKey.Create(connection.EntraTenantId, connection.EntraClientId, connection.EntraTokenScope);
        liveCredentialCache.Store(cacheKey, new StubTokenCredential(FakeJwt));
        var factory = new AzureFoundryChatClientFactory(entraLiveCredentialCache: liveCredentialCache);
        using var capture = new RequestCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var openAiClient = factory.CreateOpenAiV1ClientForTesting(connection, httpClient);
        await SendCannedChatRequestAsync(openAiClient);

        var request = AssertEx.NotNull(capture.LastRequest, "the chat request must have reached the fake transport");
        AssertEx.True(request.Headers.TryGetValues("Authorization", out var authValues));
        AssertEx.Equal($"Bearer {FakeJwt}", authValues!.Single());
        AssertEx.True(request.Headers.TryGetValues("X-Tenant", out var tenantValues));
        AssertEx.Equal("tenant-a", tenantValues!.Single());
    }

    [Test]
    public async Task ApiKey_SendsRealKeyOnApiKeyHeader_WithNoAuthorizationHeaderAtAll()
    {
        var connection = CreateApiKeyConnection("real-secret-key");
        var factory = new AzureFoundryChatClientFactory();
        using var capture = new RequestCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var openAiClient = factory.CreateOpenAiV1ClientForTesting(connection, httpClient);
        await SendCannedChatRequestAsync(openAiClient);

        var request = AssertEx.NotNull(capture.LastRequest, "the chat request must have reached the fake transport");
        AssertEx.True(request.Headers.TryGetValues("api-key", out var apiKeyValues), "api-key header must be present");
        AssertEx.Equal("real-secret-key", apiKeyValues!.Single());

        // The v1 ApiKey path constructs OpenAIClient with ApiKeyAuthenticationPolicy targeting ONLY "api-key" (no
        // prefix) as the ctor's fixed AuthenticationPolicy, so nothing in the pipeline ever writes Authorization —
        // no placeholder value is left for an AAD-validating gateway to reject if it inspects Authorization when
        // present (see AzureFoundryChatClientFactory.BuildOpenAiV1KeyCredentialClient's remarks).
        AssertEx.False(request.Headers.TryGetValues("Authorization", out _), "Authorization header must be absent in ApiKey mode");
        AssertEx.Equal(new Uri("https://example.openai.azure.com/openai/v1/chat/completions"), request.RequestUri);
    }

    [Test]
    public async Task ApiKey_ComposesWithCustomHeaders_WithoutEitherClobberingTheOther()
    {
        var connection = CreateApiKeyConnection("real-secret-key",
            headers:
            [
                new StoredAzureFoundryHeader
                {
                    Name = "X-Tenant",
                    Value = "tenant-a",
                    IsSecret = false
                }
            ]);
        var factory = new AzureFoundryChatClientFactory();
        using var capture = new RequestCapturingHandler();
        using var httpClient = new HttpClient(capture);

        var openAiClient = factory.CreateOpenAiV1ClientForTesting(connection, httpClient);
        await SendCannedChatRequestAsync(openAiClient);

        var request = AssertEx.NotNull(capture.LastRequest, "the chat request must have reached the fake transport");
        AssertEx.True(request.Headers.TryGetValues("api-key", out var apiKeyValues));
        AssertEx.Equal("real-secret-key", apiKeyValues!.Single());
        AssertEx.True(request.Headers.TryGetValues("X-Tenant", out var tenantValues));
        AssertEx.Equal("tenant-a", tenantValues!.Single());
    }

    // The engine's own resilience owns retries; an SDK retry inside each attempt multiplied one send by four, and waited out a long Retry-After.
    [Test]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    public async Task OneSend_OnTheV1Surface_IsOneRequest(HttpStatusCode status)
    {
        using var transport = new FailingTransport(status);
        using var httpClient = new HttpClient(transport);
        var openAiClient = new AzureFoundryChatClientFactory().CreateOpenAiV1ClientForTesting(CreateApiKeyConnection(), httpClient);

        await AssertEx.ThrowsAsync<ClientResultException>(() => openAiClient.GetChatClient("gpt-4o").AsIChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        AssertEx.Equal(expected: 1, transport.RequestCount);
    }

    [Test]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    public async Task OneSend_OnTheDeploymentsSurface_IsOneRequest(HttpStatusCode status)
    {
        using var transport = new FailingTransport(status);
        using var httpClient = new HttpClient(transport);
        var azureClient = new AzureFoundryChatClientFactory().CreateAzureDeploymentsClientForTesting(CreateApiKeyConnection(), httpClient);

        await AssertEx.ThrowsAsync<ClientResultException>(() => azureClient.GetChatClient("gpt-4o").AsIChatClient().GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        AssertEx.Equal(expected: 1, transport.RequestCount);
    }

    // The real SDK over each surface, buffered and streaming: the translated quota 429 keeps its status, header and body for the classifier, and the send is one request.
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AQuota429_ThroughTheTranslator_IsOneRequestAndNamesTheWait(bool deployments, bool streaming)
    {
        using var transport = new FailingTransport(HttpStatusCode.TooManyRequests,
            """{"error":"token_quota_exceeded","message":"Token limit is exceeded. Try again in 2678400 seconds.","reason":"project-quota"}""", "2678400");
        using var httpClient = new HttpClient(transport);
        var factory = new AzureFoundryChatClientFactory();
        var inner = deployments
            ? factory.CreateAzureDeploymentsClientForTesting(CreateApiKeyConnection(), httpClient).GetChatClient("gpt-4o").AsIChatClient()
            : factory.CreateOpenAiV1ClientForTesting(CreateApiKeyConnection(), httpClient).GetChatClient("gpt-4o").AsIChatClient();
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var thrown = await AssertEx.ThrowsAsync<AzureFoundryProviderException>(async () =>
        {
            if (streaming)
            {
                await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
                {
                    // The 429 surfaces before any update.
                }
            }
            else
            {
                await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            }
        });
        var classification = InvocationFailureClassifier.MapFailure(thrown);

        AssertEx.Equal(expected: 1, transport.RequestCount);
        AssertEx.Equal(FailureCategory.ProviderRateLimited, classification.Category);
        AssertEx.Equal("The token quota for this connection is used up, retry after about 31 days. (token_quota_exceeded: project-quota)", classification.Message);
    }

    private static StoredAzureFoundryConnection CreateApiKeyConnection()
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiKey = "real-secret-key",
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        };
    }

    private sealed class FailingTransport : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string? _retryAfter;
        private readonly HttpStatusCode _status;
        private int _requestCount;

        public FailingTransport(HttpStatusCode status, string body = "{}", string? retryAfter = null)
        {
            _status = status;
            _body = body;
            _retryAfter = retryAfter;
        }

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
            if (_retryAfter is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", _retryAfter);
            }

            return Task.FromResult(response);
        }
    }

    private static async Task SendCannedChatRequestAsync(OpenAIClient openAiClient)
    {
        var chatClient = openAiClient.GetChatClient("gpt-4o").AsIChatClient();

        try
        {
            await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        }
        catch (Exception)
        {
            // The canned response is a minimal stub the SDK may or may not fully deserialize; only the captured
            // REQUEST (recorded before any response parsing) matters here, matching the sibling Codex wire tests.
        }
    }

    private static StoredAzureFoundryConnection CreateApiKeyConnection(string apiKey, IReadOnlyList<StoredAzureFoundryHeader>? headers = null)
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiSurface = AzureFoundryApiSurface.OpenAiV1,
            ApiKey = apiKey,
            Headers = headers ?? [],
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        };
    }

    private static StoredAzureFoundryConnection CreateEntraConnection(IReadOnlyList<StoredAzureFoundryHeader>? headers = null)
    {
        return new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.EntraId,
            ApiSurface = AzureFoundryApiSurface.OpenAiV1,
            EntraTenantId = "tenant-id",
            EntraClientId = "client-id",
            EntraClientSecret = null,
            EntraTokenScope = "api://backend-app/.default",
            EntraSignInMethod = EntraSignInMethod.DeviceCode,
            Headers = headers ?? [],
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o"
                }
            ]
        };
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly string _tokenValue;

        public StubTokenCredential(string tokenValue)
        {
            _tokenValue = tokenValue;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken(_tokenValue, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    /// <summary>Captures the last outbound request and returns a minimal canned chat-completion reply.</summary>
    private sealed class RequestCapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            const string CannedResponse =
                """{"id":"chatcmpl-test","object":"chat.completion","created":0,"model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, Encoding.UTF8, "application/json")
            });
        }
    }
}
