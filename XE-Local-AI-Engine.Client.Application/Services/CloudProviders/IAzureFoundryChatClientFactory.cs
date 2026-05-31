namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.AI;

/// <summary>
///     Creates chat clients for persisted Azure Foundry / Azure OpenAI credentials.
/// </summary>
public interface IAzureFoundryChatClientFactory
{
    /// <summary>
    ///     Builds a provider-neutral <see cref="IChatClient" /> for the configured Azure endpoint, API key, and
    ///     deployment name.
    /// </summary>
    IChatClient Create(StoredCloudCredentials credentials);
}
