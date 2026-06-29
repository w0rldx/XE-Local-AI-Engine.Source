namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

/// <summary>
///     Azure Foundry chat-client factory backed by the Azure OpenAI .NET client.
/// </summary>
/// <remarks>
///     The rest of the node runtime consumes the returned <see cref="IChatClient" /> abstraction, which lets local
///     Ollama and cloud-backed deployments share the same agent pipeline. The returned client is wrapped in
///     <see cref="AzureFoundryErrorTranslatingChatClient" /> so an Azure <c>RequestFailedException</c> (content filter,
///     auth) surfaces as a typed <see cref="AzureFoundryProviderException" /> with a sanitized message.
/// </remarks>
public sealed class AzureFoundryChatClientFactory : IAzureFoundryChatClientFactory
{
    /// <inheritdoc />
    public IChatClient Create(StoredAzureFoundryConnection connection, string deploymentName)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(deploymentName))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An Azure Foundry deployment name must be provided.");
        }

        var endpoint = ResolveEndpoint(connection.Endpoint);

        var azureClient = connection.AuthMode switch
        {
            AzureFoundryAuthMode.ApiKey => BuildKeyCredentialClient(endpoint, connection.ApiKey),
            AzureFoundryAuthMode.ManagedIdentity => new AzureOpenAIClient(endpoint, new DefaultAzureCredential()),
            _ => throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry connection has an unsupported authentication mode.")
        };

        var innerClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
        return new AzureFoundryErrorTranslatingChatClient(innerClient);
    }

    // Validates the endpoint is absolute-HTTPS AND ends with a known Azure host suffix before it is ever handed to the
    // Azure client. The host allowlist matters most for managed identity: a DefaultAzureCredential Entra token must
    // never be sent to an arbitrary operator-entered host (MEDIUM-4).
    private static Uri ResolveEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry endpoint must be an absolute HTTPS URL.");
        }

        if (!AzureFoundryEndpoints.IsAllowedHost(uri))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry endpoint host is not an allowed Azure host.");
        }

        return uri;
    }

    private static AzureOpenAIClient BuildKeyCredentialClient(Uri endpoint, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An API key is required when the Azure Foundry connection uses API-key authentication.");
        }

        return new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));
    }
}
