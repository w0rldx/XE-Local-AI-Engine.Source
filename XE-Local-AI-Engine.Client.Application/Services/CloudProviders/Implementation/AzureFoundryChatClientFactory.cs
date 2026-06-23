namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

/// <summary>
///     Azure Foundry chat-client factory backed by the Azure OpenAI .NET client.
/// </summary>
/// <remarks>
///     The rest of the node runtime consumes the returned <see cref="IChatClient" /> abstraction, which lets local
///     Ollama and cloud-backed deployments share the same agent pipeline.
/// </remarks>
public sealed class AzureFoundryChatClientFactory : IAzureFoundryChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(StoredCloudCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (string.IsNullOrWhiteSpace(credentials.Endpoint))
        {
            throw new ArgumentException("Cloud credential endpoint must be provided.", nameof(credentials));
        }

        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new ArgumentException("Cloud credential API key must be provided.", nameof(credentials));
        }

        if (string.IsNullOrWhiteSpace(credentials.DeploymentName))
        {
            throw new ArgumentException("Cloud credential deployment name must be provided.", nameof(credentials));
        }

        var endpoint = new Uri(credentials.Endpoint, UriKind.Absolute);
        var azureClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(credentials.ApiKey));

        return azureClient.GetChatClient(credentials.DeploymentName).AsIChatClient();
    }
}
