namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;

using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

public sealed class AzureFoundryChatClientFactory : IAzureFoundryChatClientFactory
{
    public IChatClient Create(StoredCloudCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.DeploymentName);

        var endpoint = new Uri(credentials.Endpoint, UriKind.Absolute);
        var azureClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(credentials.ApiKey));

        return azureClient.GetChatClient(credentials.DeploymentName).AsIChatClient();
    }
}
