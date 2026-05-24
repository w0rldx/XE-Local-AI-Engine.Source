namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed record CloudSettingsResponse
{
    public static CloudSettingsResponse Empty { get; } = new()
    {
        ProviderName = CloudProviderOptions.ProviderNone,
        Endpoint = null,
        DeploymentName = null,
        HasStoredApiKey = false
    };

    public required string ProviderName { get; init; }

    public string? Endpoint { get; init; }

    public string? DeploymentName { get; init; }

    public bool HasStoredApiKey { get; init; }
}

public sealed record SaveCloudSettingsRequest
{
    public string ProviderName { get; init; } = CloudProviderOptions.ProviderAzureFoundry;

    public string? Endpoint { get; init; }

    public string? ApiKey { get; init; }

    public string? DeploymentName { get; init; }
}

internal static class CloudSettingsEndpointDtoMapper
{
    public static CloudSettingsResponse ToResponse(this StoredCloudCredentials? credentials)
    {
        if (credentials is null)
        {
            return CloudSettingsResponse.Empty;
        }

        return new CloudSettingsResponse
        {
            ProviderName = credentials.ProviderName,
            Endpoint = credentials.Endpoint,
            DeploymentName = credentials.DeploymentName,
            HasStoredApiKey = !string.IsNullOrWhiteSpace(credentials.ApiKey)
        };
    }

    public static StoredCloudCredentials ToStoredCredentials(this SaveCloudSettingsRequest request)
    {
        return new StoredCloudCredentials
        {
            ProviderName = request.ProviderName.Trim(),
            Endpoint = request.Endpoint?.Trim() ?? string.Empty,
            ApiKey = request.ApiKey?.Trim() ?? string.Empty,
            DeploymentName = request.DeploymentName?.Trim() ?? string.Empty
        };
    }
}
