namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     A single Azure Foundry / Azure OpenAI connection holding N manually-added deployments.
/// </summary>
/// <remarks>
///     No <c>required</c> members so a partial / legacy JSON parse never throws (HIGH-2). The API key is
///     null when <see cref="AuthMode" /> is <see cref="AzureFoundryAuthMode.ManagedIdentity" />.
/// </remarks>
public sealed record StoredAzureFoundryConnection
{
    /// <summary>
    ///     The connection endpoint (absolute HTTPS, Azure host).
    /// </summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>
    ///     The authentication mode for this connection.
    /// </summary>
    public AzureFoundryAuthMode AuthMode { get; init; }

    /// <summary>
    ///     The encrypted API key. Null when <see cref="AuthMode" /> is
    ///     <see cref="AzureFoundryAuthMode.ManagedIdentity" />.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    ///     The deployments manually added to this connection.
    /// </summary>
    public IReadOnlyList<StoredAzureFoundryModel> Models { get; init; } = [];
}
