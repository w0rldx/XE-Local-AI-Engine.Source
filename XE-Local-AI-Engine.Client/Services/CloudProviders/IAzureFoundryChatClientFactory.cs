namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.AI;

public interface IAzureFoundryChatClientFactory
{
    IChatClient Create(StoredCloudCredentials credentials);
}
