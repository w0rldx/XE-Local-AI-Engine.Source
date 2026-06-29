namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed record CloudSettingsResponse
{
    public static CloudSettingsResponse Empty { get; } = new()
    {
        ProviderName = CloudProviderOptions.ProviderNone,
        AzureFoundry = null
    };

    public required string ProviderName { get; init; }

    /// <summary>
    ///     The stored Azure Foundry connection (endpoint, auth mode, models). Null when no Azure connection is
    ///     configured. The API key is never surfaced — only <see cref="AzureFoundrySettingsResponse.HasStoredApiKey" />.
    /// </summary>
    public AzureFoundrySettingsResponse? AzureFoundry { get; init; }
}

public sealed record AzureFoundrySettingsResponse
{
    public string? Endpoint { get; init; }

    /// <summary>The persisted auth mode as the <see cref="AzureFoundryAuthMode" /> enum name (ApiKey | ManagedIdentity).</summary>
    public required string AuthMode { get; init; }

    /// <summary>True when an API key is stored. The key itself is never returned to the client.</summary>
    public bool HasStoredApiKey { get; init; }

    public IReadOnlyList<AzureFoundryModelDto> Models { get; init; } = [];
}

public sealed record AzureFoundryModelDto
{
    public required string DeploymentName { get; init; }

    public string? DisplayLabel { get; init; }
}

public sealed record SaveCloudSettingsRequest
{
    public string ProviderName { get; init; } = CloudProviderOptions.ProviderAzureFoundry;

    public string? Endpoint { get; init; }

    /// <summary>The requested auth mode (ApiKey | ManagedIdentity). Case-insensitive enum name.</summary>
    public string AuthMode { get; init; } = nameof(AzureFoundryAuthMode.ApiKey);

    /// <summary>Required only when <see cref="AuthMode" /> is <c>ApiKey</c>; ignored for managed identity.</summary>
    public string? ApiKey { get; init; }

    public IReadOnlyList<AzureFoundryModelDto> Models { get; init; } = [];
}

internal static class CloudSettingsEndpointDtoMapper
{
    public static CloudSettingsResponse ToResponse(this StoredCloudProviderConfig? config)
    {
        if (config?.AzureFoundry is not { } connection)
        {
            return CloudSettingsResponse.Empty;
        }

        return new CloudSettingsResponse
        {
            ProviderName = config.ProviderName,
            AzureFoundry = new AzureFoundrySettingsResponse
            {
                Endpoint = string.IsNullOrWhiteSpace(connection.Endpoint) ? null : connection.Endpoint,
                AuthMode = connection.AuthMode.ToString(),
                // Never emit the stored key — presence only.
                HasStoredApiKey = !string.IsNullOrWhiteSpace(connection.ApiKey),
                Models =
                [
                    .. connection.Models.Select(static model => new AzureFoundryModelDto
                    {
                        DeploymentName = model.DeploymentName,
                        DisplayLabel = model.DisplayLabel
                    })
                ]
            }
        };
    }

    public static StoredCloudProviderConfig ToStoredConfig(this SaveCloudSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authMode = ParseAuthMode(request.AuthMode);

        return new StoredCloudProviderConfig
        {
            SchemaVersion = 2,
            ProviderName = request.ProviderName.Trim(),
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = request.Endpoint?.Trim() ?? string.Empty,
                AuthMode = authMode,
                // Managed identity carries no key; drop anything supplied so it is never persisted.
                ApiKey = authMode == AzureFoundryAuthMode.ApiKey
                    ? NormalizeApiKey(request.ApiKey)
                    : null,
                Models =
                [
                    .. request.Models.Select(static model => new StoredAzureFoundryModel
                    {
                        DeploymentName = model.DeploymentName?.Trim() ?? string.Empty,
                        DisplayLabel = string.IsNullOrWhiteSpace(model.DisplayLabel) ? null : model.DisplayLabel.Trim()
                    })
                ]
            }
        };
    }

    private static AzureFoundryAuthMode ParseAuthMode(string? authMode)
    {
        return Enum.TryParse<AzureFoundryAuthMode>(authMode?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : AzureFoundryAuthMode.ApiKey;
    }

    private static string? NormalizeApiKey(string? apiKey)
    {
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }
}
