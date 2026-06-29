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
}
