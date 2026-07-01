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

    /// <summary>
    ///     Custom request headers on this connection. Secret header values are never returned (Value is null); presence
    ///     is signalled by <see cref="AzureFoundryHeaderDto.HasStoredValue" />.
    /// </summary>
    public IReadOnlyList<AzureFoundryHeaderDto> Headers { get; init; } = [];

    /// <summary>Operator-added extra allowed host suffixes (Locked #14). Not secret — round-trips for inline editing.</summary>
    public IReadOnlyList<string> AdditionalAllowedHostSuffixes { get; init; } = [];
}

public sealed record AzureFoundryModelDto
{
    public required string DeploymentName { get; init; }

    public string? DisplayLabel { get; init; }
}

public sealed record AzureFoundryHeaderDto
{
    public required string Name { get; init; }

    /// <summary>The header value for a non-secret header; always null for a secret header (write-only).</summary>
    public string? Value { get; init; }

    public bool IsSecret { get; init; }

    /// <summary>True when a value is stored for this header (used to show a "stored" placeholder for secret headers).</summary>
    public bool HasStoredValue { get; init; }
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

    /// <summary>
    ///     Custom request headers to persist. A secret header sent with a blank value keeps the previously stored value
    ///     (merge happens in the endpoint, Locked #10/#12).
    /// </summary>
    public IReadOnlyList<SaveAzureFoundryHeaderRequest> Headers { get; init; } = [];

    /// <summary>Operator-added extra allowed host suffixes (Locked #14). Shape-validated on save.</summary>
    public IReadOnlyList<string> AdditionalAllowedHostSuffixes { get; init; } = [];
}

public sealed record SaveAzureFoundryHeaderRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Value { get; init; }

    public bool IsSecret { get; init; }
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
                ],
                Headers =
                [
                    .. connection.Headers.Select(static header => new AzureFoundryHeaderDto
                    {
                        Name = header.Name,
                        // Never emit a secret value — presence only. Non-secret values round-trip for inline editing.
                        Value = header.IsSecret ? null : header.Value,
                        IsSecret = header.IsSecret,
                        HasStoredValue = !string.IsNullOrWhiteSpace(header.Value)
                    })
                ],
                AdditionalAllowedHostSuffixes = [.. connection.AdditionalAllowedHostSuffixes]
            }
        };
    }

    /// <summary>
    ///     Maps a save request to the stored config. Pure: the secret-header merge that needs prior state runs in the
    ///     endpoint and is passed in via <paramref name="mergedHeaders" /> (Locked #12).
    /// </summary>
    public static StoredCloudProviderConfig ToStoredConfig(this SaveCloudSettingsRequest request,
        IReadOnlyList<StoredAzureFoundryHeader> mergedHeaders)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergedHeaders);

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
                ],
                Headers = mergedHeaders,
                AdditionalAllowedHostSuffixes =
                [
                    .. request.AdditionalAllowedHostSuffixes
                        .Select(static suffix => suffix?.Trim() ?? string.Empty)
                        .Where(static suffix => suffix.Length > 0)
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
