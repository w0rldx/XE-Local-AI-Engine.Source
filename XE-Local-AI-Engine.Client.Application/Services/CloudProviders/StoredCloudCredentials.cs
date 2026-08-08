namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed record StoredCloudCredentials
{
    public required string ProviderName { get; init; }

    public required string Endpoint { get; init; }

    public required string ApiKey { get; init; }

    public required string DeploymentName { get; init; }
}
