namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed record NodeSettingsResponse
{
    public int MaxMessageRequestTimeoutSeconds { get; init; }

    public string? DefaultModelName { get; init; }

    public int MinMessageRequestTimeoutSeconds { get; init; }

    public int MaxAllowedMessageRequestTimeoutSeconds { get; init; }
}

public sealed record SaveNodeSettingsRequest
{
    public int MaxMessageRequestTimeoutSeconds { get; init; }

    public string? DefaultModelName { get; init; }
}

internal static class NodeSettingsEndpointDtoMapper
{
    public static NodeSettingsResponse ToResponse(this StoredNodeSettings settings)
    {
        return new NodeSettingsResponse
        {
            MaxMessageRequestTimeoutSeconds = settings.MaxMessageRequestTimeoutSeconds,
            DefaultModelName = settings.DefaultModelName,
            MinMessageRequestTimeoutSeconds = StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds,
            MaxAllowedMessageRequestTimeoutSeconds = StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds
        };
    }

    public static StoredNodeSettings ToStoredSettings(this SaveNodeSettingsRequest request, StoredNodeSettings currentSettings)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);

        return new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = request.MaxMessageRequestTimeoutSeconds,
            DefaultModelName = request.DefaultModelName is null
                ? currentSettings.DefaultModelName
                : request.DefaultModelName.Trim()
        };
    }
}
