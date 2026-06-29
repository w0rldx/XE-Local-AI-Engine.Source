namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Azure;
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
                new StoredAzureFoundryModel { DeploymentName = "gpt-4o", DisplayLabel = "GPT-4o" },
                new StoredAzureFoundryModel { DeploymentName = "gpt-4o-mini" },
                new StoredAzureFoundryModel { DeploymentName = "  " }
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
        var requestFailed = new RequestFailedException(status: 400, message: "The response was filtered due to the prompt triggering the content management policy.", errorCode: "content_filter", innerException: null);
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
            [System.Runtime.CompilerServices.EnumeratorCancellation]
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
