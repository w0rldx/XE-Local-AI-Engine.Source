namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Authentication mode for an Azure Foundry / Azure OpenAI connection.
/// </summary>
public enum AzureFoundryAuthMode
{
    /// <summary>
    ///     Authenticate with a connection-scoped API key.
    /// </summary>
    ApiKey = 0,

    /// <summary>
    ///     Authenticate with <c>DefaultAzureCredential</c> (Entra managed identity / developer credential).
    /// </summary>
    ManagedIdentity = 1,

    /// <summary>
    ///     Authenticate with a self-fetched Entra ID bearer token: app-only client-credentials when a client secret
    ///     is configured, or interactive user sign-in (device code / interactive browser) otherwise. Intended for an
    ///     Azure API Management AI gateway that validates the bearer token itself rather than an Azure OpenAI /
    ///     Foundry resource key or managed-identity RBAC role.
    /// </summary>
    EntraId = 2,
}
