namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using Azure;
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
}
