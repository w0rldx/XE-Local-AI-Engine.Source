namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Value object carrying stored cloud credentials data.
/// </summary>
public sealed record StoredCloudCredentials
{
    public required string ProviderName { get; init; }

    public required string Endpoint { get; init; }

    public required string ApiKey { get; init; }

    public required string DeploymentName { get; init; }
}
