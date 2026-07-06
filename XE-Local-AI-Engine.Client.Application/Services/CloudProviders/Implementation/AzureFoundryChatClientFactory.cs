namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel;
using System.ClientModel.Primitives;
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

        var endpoint = ResolveEndpoint(connection.Endpoint, connection.AdditionalAllowedHostSuffixes);

        var options = BuildClientOptions(connection.Headers);

        var azureClient = connection.AuthMode switch
        {
            AzureFoundryAuthMode.ApiKey => BuildKeyCredentialClient(endpoint, connection.ApiKey, options),
            AzureFoundryAuthMode.ManagedIdentity => new AzureOpenAIClient(endpoint, new DefaultAzureCredential(), options),
            _ => throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry connection has an unsupported authentication mode.")
        };

        var innerClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
        return new AzureFoundryErrorTranslatingChatClient(innerClient);
    }

    // Validates the endpoint is absolute-HTTPS AND ends with a known Azure host suffix before it is ever handed to the
    // Azure client. The host allowlist matters most for managed identity: a DefaultAzureCredential Entra token must
    // never be sent to an arbitrary operator-entered host (MEDIUM-4).
    private static Uri ResolveEndpoint(string? endpoint, IReadOnlyList<string> extraAllowedHostSuffixes)
    {
        if (string.IsNullOrWhiteSpace(endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry endpoint must be an absolute HTTPS URL.");
        }

        if (!AzureFoundryEndpoints.IsAllowedHost(uri, extraAllowedHostSuffixes))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "The Azure Foundry endpoint host is not an allowed Azure host.");
        }

        return uri;
    }

    // Attaches the custom-header policy at PerCall when the connection carries headers (Locked #4). Reserved names are
    // skipped inside the policy; blank-name rows are dropped here. Diagnostics.IsLoggingContentEnabled is left unset
    // so secret header values are never logged by the SDK (security LOW-6).
    private static AzureOpenAIClientOptions BuildClientOptions(IReadOnlyList<StoredAzureFoundryHeader> headers)
    {
        var options = new AzureOpenAIClientOptions();

        var resolved = ResolveHeaders(headers);
        if (resolved.Count > 0)
        {
            options.AddPolicy(new CustomHeaderPipelinePolicy(resolved), PipelinePosition.PerCall);
        }

        return options;
    }

    private static IReadOnlyList<(string Name, string Value)> ResolveHeaders(IReadOnlyList<StoredAzureFoundryHeader> headers)
    {
        return
        [
            .. headers
               .Where(static header => !string.IsNullOrWhiteSpace(header.Name))
               .Select(static header => (header.Name.Trim(), header.Value ?? string.Empty))
        ];
    }

    private static AzureOpenAIClient BuildKeyCredentialClient(Uri endpoint, string? apiKey, AzureOpenAIClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new AzureFoundryProviderException(AzureFoundryProviderErrorKind.Configuration,
                "An API key is required when the Azure Foundry connection uses API-key authentication.");
        }

        return new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey), options);
    }
}
