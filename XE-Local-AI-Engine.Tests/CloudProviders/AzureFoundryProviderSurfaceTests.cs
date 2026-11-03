namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the two remaining Azure Foundry surfaces: the model-list mapper that publishes manually-added
///     deployments into the chat picker, and the error-translating chat client that turns an Azure
///     <see cref="RequestFailedException" /> into a typed, secret-free <see cref="AzureFoundryProviderException" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AzureFoundryProviderSurfaceTests
{
    private const string ApiKeyText = "Azure Foundry rejected the credentials (check the API key or the managed-identity RBAC role).";
    private const string DeniedText = "The provider or gateway denied this request. Check with its admin what this connection may use.";

    private const string RbacDeniedText =
        "Azure Foundry denied this identity the model or operation. Check its RBAC role on the resource, or ask the gateway's admin if a gateway fronts it.";

    [Test]
    public void ToAzureFoundryCloudModelResponses_MapsEachDeployment_TaggedAzureFoundryAndToolCapable()
    {
        var connection = new StoredAzureFoundryConnection
        {
            Endpoint = "https://example.openai.azure.com/",
            AuthMode = AzureFoundryAuthMode.ApiKey,
            ApiKey = "k",
            Models =
            [
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o",
                    DisplayLabel = "GPT-4o"
                },
                new StoredAzureFoundryModel
                {
                    DeploymentName = "gpt-4o-mini"
                },
                new StoredAzureFoundryModel
                {
                    DeploymentName = "  "
                }
            ]
        };

        var responses = LocalModelsMapper.ToAzureFoundryCloudModelResponses(connection, selectedModelName: "gpt-4o");

        // Blank deployment filtered out; both real deployments surface under the AzureFoundry provider group.
        AssertEx.Equal(expected: 2, responses.Count);
        AssertEx.True(responses.All(model => model.Provider == LocalModelProviders.AzureFoundry));
        AssertEx.True(responses.All(model => model.IsToolCapable));
        var selected = responses.Single(model => model.ModelName == "gpt-4o");
        AssertEx.True(selected.IsSelected);
        AssertEx.False(responses.Single(model => model.ModelName == "gpt-4o-mini").IsSelected);
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenContentFilter400_ThrowsContentFiltered_WithoutSecret()
    {
        var requestFailed = new RequestFailedException(status: 400, message: "The response was filtered due to the prompt triggering the content management policy.", errorCode: "content_filter",
            innerException: null);
        using var inner = new ThrowingChatClient(requestFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.ContentFiltered, error.Kind);
        AssertEx.False(error.Message.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenAuth401_ThrowsAuthFailed()
    {
        var requestFailed = new RequestFailedException(status: 401, message: "Unauthorized", errorCode: "Unauthorized", innerException: null);
        using var inner = new ThrowingChatClient(requestFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
    }

    // Mirrors the real Azure.Identity/MSAL shape (verified live): the outer AuthenticationFailedException.Message
    // is just a generic "...authentication failed: " preamble — the actionable AADSTS reason lives on the INNER
    // exception's message instead (a real run has an MsalServiceException there); the " ---> " chain seen in a
    // logged ToString() is only how .NET renders nested exceptions, not part of any single Message. The inner
    // message here also carries a trailing trace/secret line to prove that line is dropped, never surfaced.
    private static AuthenticationFailedException CreateRealisticEntraAuthFailure()
    {
        // A real run has an MSAL MsalServiceException here; InvalidOperationException stands in as a stub inner
        // exception (CA2201 forbids constructing the base System.Exception type directly) — only the Message
        // matters for this test, not the concrete type.
        var inner = new InvalidOperationException("Microsoft.Identity.Client.MsalServiceException: AADSTS1002012: The provided value for scope " +
                                                  "'api://backend-app/access_as_user' is not valid. Client credential flows must have a scope value " +
                                                  "with /.default suffix.\n" +
                                                  "Trace: client_secret=super-secret-client-secret-value at Msal.Internal.Foo()");
        return new AuthenticationFailedException("ClientSecretCredential authentication failed: ", inner);
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenEntraAuthenticationFails_ThrowsAuthFailed_WithSanitizedMessage()
    {
        var authFailed = CreateRealisticEntraAuthFailure();
        using var inner = new ThrowingChatClient(authFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
        AssertEx.True(error.Message.Contains("authentication failed", StringComparison.Ordinal));
        AssertEx.True(error.Message.Contains("AADSTS1002012", StringComparison.Ordinal));
        AssertEx.False(error.Message.Contains("Trace:", StringComparison.Ordinal));
        AssertEx.False(error.Message.Contains("super-secret-client-secret-value", StringComparison.Ordinal));
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenEntraAuthenticationFailsDuringStreaming_ThrowsAuthFailed_WithSanitizedMessage()
    {
        var authFailed = CreateRealisticEntraAuthFailure();
        using var inner = new ThrowingChatClient(authFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                // The exception is thrown before any update is yielded (see ThrowingChatClient); nothing to do here.
            }
        });

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
        AssertEx.True(error.Message.Contains("authentication failed", StringComparison.Ordinal));
        AssertEx.True(error.Message.Contains("AADSTS1002012", StringComparison.Ordinal));
        AssertEx.False(error.Message.Contains("Trace:", StringComparison.Ordinal));
        AssertEx.False(error.Message.Contains("super-secret-client-secret-value", StringComparison.Ordinal));
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenTransportErrorHasJsonBody_AppendsSanitizedDetail()
    {
        using var response = new FakeAzureResponse(500, BinaryData.FromString("""{"error":{"code":"PolicyFailed","message":"APIM policy blocked the request."}}"""));
        var requestFailed = new RequestFailedException(response);
        using var inner = new ThrowingChatClient(requestFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.Transport, error.Kind);
        AssertEx.True(error.Message.Contains("APIM policy blocked the request.", StringComparison.Ordinal));
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenTransportErrorHasNoBody_DoesNotAppendDetail()
    {
        var requestFailed = new RequestFailedException(status: 500, message: "boom", errorCode: null, innerException: null);
        using var inner = new ThrowingChatClient(requestFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.Transport, error.Kind);
        AssertEx.Equal("The Azure Foundry endpoint returned an error (HTTP 500).", error.Message);
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenOpenAiV1TransportErrorHasJsonBody_ThrowsTransport_WithSanitizedDetail()
    {
        using var response = new FakePipelineResponse(500, BinaryData.FromString("""{"error":{"code":"PolicyFailed","message":"Gateway rejected the request."}}"""));
        var clientResultException = new ClientResultException(response, innerException: null);
        using var inner = new ThrowingChatClient(clientResultException);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.Transport, error.Kind);
        AssertEx.True(error.Message.Contains("Gateway rejected the request.", StringComparison.Ordinal));
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenOpenAiV1TransportErrorHasJsonBodyDuringStreaming_ThrowsTransport_WithSanitizedDetail()
    {
        // Streaming parity for ErrorTranslatingChatClient_WhenOpenAiV1TransportErrorHasJsonBody_ThrowsTransport_WithSanitizedDetail
        // above — the ClientResultException catch inside GetStreamingResponseAsync's per-MoveNextAsync try/catch must
        // extract and cap the same body detail as the non-streaming path.
        var longMessage = new string('x', 400);
        var body = """{"error":{"code":"PolicyFailed","message":"REPLACE_ME"}}""".Replace("REPLACE_ME", longMessage, StringComparison.Ordinal);
        using var response = new FakePipelineResponse(500, BinaryData.FromString(body));
        var clientResultException = new ClientResultException(response, innerException: null);
        using var inner = new ThrowingChatClient(clientResultException);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
            {
                // The exception is thrown before any update is yielded (see ThrowingChatClient); nothing to do here.
            }
        });

        AssertEx.Equal(AzureFoundryProviderErrorKind.Transport, error.Kind);
        AssertEx.True(error.Message.Contains(new string('x', 300), StringComparison.Ordinal));
        AssertEx.False(error.Message.Contains(longMessage, StringComparison.Ordinal));
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenOpenAiV1ContentFilter400_ThrowsContentFiltered()
    {
        using var response = new FakePipelineResponse(400, BinaryData.FromString("""{"error":{"code":"content_filter","message":"The response was filtered."}}"""));
        var clientResultException = new ClientResultException(response, innerException: null);
        using var inner = new ThrowingChatClient(clientResultException);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.ContentFiltered, error.Kind);
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenOpenAiV1Auth401_ThrowsAuthFailed()
    {
        using var response = new FakePipelineResponse(401, content: null);
        var clientResultException = new ClientResultException(response, innerException: null);
        using var inner = new ThrowingChatClient(clientResultException);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
    }

    [Test]
    [Arguments(AzureFoundryAuthMode.ApiKey, 401, "Azure Foundry rejected the credentials (check the API key or the managed-identity RBAC role).")]
    [Arguments(AzureFoundryAuthMode.EntraId, 401, "Azure Foundry rejected the Entra ID bearer token (sign in again, or check the token scope).")]
    [Arguments(AzureFoundryAuthMode.ManagedIdentity, 401,
        "Azure Foundry rejected the managed-identity bearer token (check the identity's RBAC role and the token scope).")]
    [Arguments(AzureFoundryAuthMode.ApiKey, 403, DeniedText)]
    [Arguments(AzureFoundryAuthMode.EntraId, 403, RbacDeniedText)]
    [Arguments(AzureFoundryAuthMode.ManagedIdentity, 403, RbacDeniedText)]
    public async Task ErrorTranslatingChatClient_WhenAuthRejected_NamesTheCredentialOfTheAuthMode(AzureFoundryAuthMode authMode, int status, string expectedMessage)
    {
        using var response = new FakePipelineResponse(status, content: null);
        using var inner = new ThrowingChatClient(new ClientResultException(response, innerException: null));
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, authMode);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
        AssertEx.Equal(expectedMessage, error.Message);
    }

    // A gateway in front of the deployment denies for reasons of its own, and a new key would not change them: only a 401 or a credential code blames the
    // credential. An Azure RBAC refusal keeps its guidance; bodies below are this gateway's and real-shaped Azure ones. Hostile text is never quoted.
    [Test]
    [Arguments(401, """{"error":{"code":"Unauthorized","message":"Access denied due to invalid subscription key."}}""", ApiKeyText + " (Access denied due to invalid subscription key.)")]
    [Arguments(401, """{"error":"api-key-revoked","message":"The API key has been revoked."}""", ApiKeyText + " (api-key-revoked: The API key has been revoked.)")]
    [Arguments(403, """{"error":"api-key-invalid","message":"The API key is not valid."}""", ApiKeyText + " (api-key-invalid: The API key is not valid.)")]
    [Arguments(403, """{"error":"project-archived","message":"The project is archived."}""", DeniedText + " (project-archived: The project is archived.)")]
    [Arguments(403, """{"error":"model-not-approved","message":"The model is not approved for the project."}""", DeniedText + " (model-not-approved: The model is not approved for the project.)")]
    [Arguments(403, """{"error":"token_quota_exceeded","message":"Token limit is exceeded.","reason":"project-quota"}""", DeniedText + " (token_quota_exceeded: Token limit is exceeded. (reason: project-quota))")]
    [Arguments(403, """{"error":{"code":"PermissionDenied","message":"Principal does not have access to API/Operation."}}""", RbacDeniedText + " (PermissionDenied)")]
    [Arguments(403, """{"error":{"code":"PermissionDenied","message":"The principal `00000000-1111-2222-3333-444444444444` lacks the required data action `Microsoft.CognitiveServices/accounts/OpenAI/responses/write` to perform `POST /openai/v1/responses` operation."}}""", RbacDeniedText + " (PermissionDenied)")]
    [Arguments(403, """{"error":{"code":"AuthenticationTypeDisabled","message":"Key based authentication is disabled for this resource."}}""", DeniedText + " (Key based authentication is disabled for this resource.)")]
    [Arguments(403, """{"error":{"code":"403","message":"Access denied due to Virtual Network/Firewall rules."}}""", DeniedText)]
    [Arguments(403, """{"message":"See https://gateway.example/why with key sk-abc123."}""", DeniedText)]
    [Arguments(403, "<html>Forbidden</html>", DeniedText)]
    public async Task ErrorTranslatingChatClient_WhenAuthRejectedWithABody_OnlyACredentialFailureBlamesTheCredential(int status, string body, string expectedMessage)
    {
        using var response = new FakePipelineResponse(status, BinaryData.FromString(body));
        using var inner = new ThrowingChatClient(new ClientResultException(response, innerException: null));
        using var client = new AzureFoundryErrorTranslatingChatClient(inner);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
        AssertEx.Equal(expectedMessage, error.Message);
    }

    // A body echoing a credential is not quoted, wherever the credential sits, on either SDK's exception and on both statuses; the fixed text stays.
    [Test]
    [MatrixDataSource]
    public async Task ErrorTranslatingChatClient_WhenAnAuthBodyEchoesACredential_KeepsOnlyTheFixedText(
        [Matrix(401, 403)] int status,
        [Matrix("""{"error":{"message":"Invalid key %s"}}""", """{"message":"Invalid key %s"}""", """{"reason":"Invalid key %s"}""", """{"error":{"code":"%s"}}""", """{"error":"%s"}""", """{"detail":{"seen":["%s"]}}""")] string shape,
        [Matrix(false, true)] bool azureSdkException)
    {
        const string secret = "lettersonlysecret";
        var body = BinaryData.FromString(shape.Replace("%s", secret, StringComparison.Ordinal));
        using var clientResponse = new FakePipelineResponse(status, body);
        using var azureResponse = new FakeAzureResponse(status, body);
        Exception thrown = azureSdkException ? new RequestFailedException(azureResponse) : new ClientResultException(clientResponse, innerException: null);
        using var inner = new ThrowingChatClient(thrown);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, AzureFoundryAuthMode.ApiKey, ["another-value", secret]);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
        AssertEx.Equal(status == 401 ? ApiKeyText : DeniedText, error.Message);
        AssertEx.False(error.ToString().Contains(secret, StringComparison.Ordinal), $"The chain a log prints carried the credential: {error}");
    }

    // The same streaming, where a chat turn meets it.
    [Test]
    public async Task ErrorTranslatingChatClient_WhenAStreamingAuthBodyEchoesACredential_KeepsOnlyTheFixedText()
    {
        const string secret = "lettersonlysecret";
        using var response = new FakePipelineResponse(401, BinaryData.FromString($$"""{"message":"Invalid key {{secret}}"}"""));
        using var inner = new ThrowingChatClient(new ClientResultException(response, innerException: null));
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, AzureFoundryAuthMode.ApiKey, [secret]);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() => client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ToChatResponseAsync());

        AssertEx.Equal(ApiKeyText, error.Message);
    }

    // Knowing the credentials changes nothing for a body that does not carry one.
    [Test]
    [Arguments(401, """{"error":"api-key-revoked","message":"The API key has been revoked."}""", ApiKeyText + " (api-key-revoked: The API key has been revoked.)")]
    [Arguments(403, """{"message":"Project alpha is archived."}""", DeniedText + " (Project alpha is archived.)")]
    public async Task ErrorTranslatingChatClient_WhenTheBodyCarriesNoCredential_StillQuotesTheReason(int status, string body, string expectedMessage)
    {
        using var response = new FakePipelineResponse(status, BinaryData.FromString(body));
        var thrown = new ClientResultException(response, innerException: null);
        using var inner = new ThrowingChatClient(thrown);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, AzureFoundryAuthMode.ApiKey, ["lettersonlysecret"]);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(expectedMessage, error.Message);
        AssertEx.True(ReferenceEquals(thrown, error.InnerException), "The SDK exception stays on the chain when the body is clean.");
    }

    // The 5xx path quotes a raw message with no word grammar, so even a key shaped like a key got through; a 429 quotes the body later, in the classifier, off the inner exception.
    [Test]
    [Arguments(500, "sk-live-abc123")]
    [Arguments(503, "lettersonlysecret")]
    [Arguments(429, "lettersonlysecret")]
    public async Task ErrorTranslatingChatClient_WhenATransportBodyEchoesACredential_NothingReachesTheOperatorOrTheChain(int status, string secret)
    {
        using var response = new FakePipelineResponse(status, BinaryData.FromString($$$"""{"error":{"message":"Bad key {{{secret}}} sent"}}"""));
        using var inner = new ThrowingChatClient(new ClientResultException(response, innerException: null));
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, AzureFoundryAuthMode.ApiKey, [secret]);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.Transport, error.Kind);
        AssertEx.Equal($"The Azure Foundry endpoint returned an error (HTTP {status}).", error.Message);
        AssertEx.False(InvocationFailureClassifier.MapFailure(error).Message.Contains(secret, StringComparison.Ordinal), "The classified message carried the credential.");
        AssertEx.False(error.ToString().Contains(secret, StringComparison.Ordinal), $"The chain a log prints carried the credential: {error}");
    }

    // With an Entra or managed identity, a gateway's own reason still outranks the RBAC guidance a bare 403 gets.
    [Test]
    public async Task ErrorTranslatingChatClient_WhenAnIdentityIsDeniedForAGatewayReason_QuotesTheDenial()
    {
        using var response = new FakePipelineResponse(403, BinaryData.FromString("""{"error":"project-archived","message":"The project is archived."}"""));
        using var inner = new ThrowingChatClient(new ClientResultException(response, innerException: null));
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, AzureFoundryAuthMode.EntraId);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(DeniedText + " (project-archived: The project is archived.)", error.Message);
    }

    [Test]
    public async Task ErrorTranslatingChatClient_WhenEntraModeRequestFailed401_NamesTheBearerToken()
    {
        var requestFailed = new RequestFailedException(status: 401, message: "Unauthorized", errorCode: "Unauthorized", innerException: null);
        using var inner = new ThrowingChatClient(requestFailed);
        using var client = new AzureFoundryErrorTranslatingChatClient(inner, AzureFoundryAuthMode.EntraId);

        var error = await ThrowsAsync<AzureFoundryProviderException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        AssertEx.Equal(AzureFoundryProviderErrorKind.AuthFailed, error.Kind);
        AssertEx.True(error.Message.Contains("bearer token", StringComparison.Ordinal), error.Message);
        AssertEx.False(error.Message.Contains("API key", StringComparison.Ordinal), error.Message);
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException expected)
        {
            return expected;
        }

        throw new AssertionException($"Expected {typeof(TException).Name} but no exception was thrown.");
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        private readonly Exception _toThrow;

        public ThrowingChatClient(Exception toThrow)
        {
            _toThrow = toThrow;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw _toThrow;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            ExceptionDispatchInfo.Throw(_toThrow);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }

    // Minimal Azure.Response test double carrying a fixed status + JSON body, so RequestFailedException(Response)
    // exercises the real GetRawResponse().Content path the translator reads (Locked body-detail extraction).
    private sealed class FakeAzureResponse : Response
    {
        public FakeAzureResponse(int status, BinaryData content)
        {
            Status = status;
            Content = content;
        }

        public override int Status { get; }

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content { get; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name)
        {
            return false;
        }

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            return [];
        }

        protected override bool TryGetHeader(string name, out string value)
        {
            value = null!;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = null!;
            return false;
        }
    }

    // Minimal System.ClientModel.Primitives.PipelineResponse test double, the v1-surface analogue of FakeAzureResponse
    // above — carries a fixed status + optional JSON body so ClientResultException(PipelineResponse, Exception)
    // exercises the real GetRawResponse().Content path.
    private sealed class FakePipelineResponse : PipelineResponse
    {
        private static readonly BinaryData EmptyContent = BinaryData.FromBytes(ReadOnlyMemory<byte>.Empty);

        public FakePipelineResponse(int status, BinaryData? content)
        {
            Status = status;
            Content = content ?? EmptyContent;
        }

        public override int Status { get; }

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content { get; }

        protected override PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default)
        {
            return Content;
        }

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(Content);
        }

        public override void Dispose()
        {
        }
    }
}
