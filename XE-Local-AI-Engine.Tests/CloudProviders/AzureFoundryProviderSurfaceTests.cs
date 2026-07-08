namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the two remaining Azure Foundry surfaces: the model-list mapper that publishes manually-added
///     deployments into the chat picker, and the error-translating chat client that turns an Azure
///     <see cref="RequestFailedException" /> into a typed, secret-free <see cref="AzureFoundryProviderException" />.
/// </summary>
public sealed class AzureFoundryProviderSurfaceTests
{
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
        var inner = new InvalidOperationException(
            "Microsoft.Identity.Client.MsalServiceException: AADSTS1002012: The provided value for scope " +
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
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ConfigureAwait(false))
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
            await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]).ConfigureAwait(false))
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

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException expected)
        {
            return expected;
        }

        throw new AssertionException($"Expected {typeof(TException).Name} but no exception was thrown.");
    }

    private sealed class ThrowingChatClient(Exception toThrow) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw toThrow;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            throw toThrow;
#pragma warning disable CS0162 // Unreachable: satisfies the iterator contract.
            yield break;
#pragma warning restore CS0162
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
    private sealed class FakeAzureResponse(int status, BinaryData content) : Response
    {
        public override int Status { get; } = status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content { get; } = content;

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
    private sealed class FakePipelineResponse(int status, BinaryData? content) : PipelineResponse
    {
        private static readonly BinaryData EmptyContent = BinaryData.FromBytes(ReadOnlyMemory<byte>.Empty);

        public override int Status { get; } = status;

        public override string ReasonPhrase => string.Empty;

        public override Stream? ContentStream { get; set; }

        public override BinaryData Content { get; } = content ?? EmptyContent;

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
