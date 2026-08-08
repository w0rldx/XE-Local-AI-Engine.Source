namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.AI;

/// <summary>
///     Creates chat clients for persisted Azure Foundry / Azure OpenAI credentials.
/// </summary>
public interface IAzureFoundryChatClientFactory
{
    /// <summary>
    ///     Builds a provider-neutral <see cref="IChatClient" /> for the given Azure Foundry connection (endpoint +
    ///     auth mode + optional key) targeting a specific deployment. Throws
    ///     <see cref="AzureFoundryProviderException" /> (kind <see cref="AzureFoundryProviderErrorKind.Configuration" />)
    ///     when the connection cannot build a client (blank deployment, non-HTTPS or disallowed-host endpoint, or an
    ///     API-key auth mode with no key).
    /// </summary>
    IChatClient Create(StoredAzureFoundryConnection connection, string deploymentName);
}
