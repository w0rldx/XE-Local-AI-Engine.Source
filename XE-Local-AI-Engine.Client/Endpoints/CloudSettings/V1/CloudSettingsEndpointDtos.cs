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

    /// <summary>The persisted auth mode as the <see cref="AzureFoundryAuthMode" /> enum name (ApiKey | ManagedIdentity | EntraId).</summary>
    public required string AuthMode { get; init; }

    /// <summary>
    ///     The persisted wire surface as the <see cref="AzureFoundryApiSurface" /> enum name (AzureDeployments |
    ///     OpenAiV1). Defaults to <c>AzureDeployments</c> for a legacy connection with no stored surface.
    /// </summary>
    public string ApiSurface { get; init; } = nameof(AzureFoundryApiSurface.AzureDeployments);

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

    /// <summary>The Entra ID tenant id. Populated only when <see cref="AuthMode" /> is <c>EntraId</c>.</summary>
    public string? EntraTenantId { get; init; }

    /// <summary>The Entra ID application (client) id. Populated only when <see cref="AuthMode" /> is <c>EntraId</c>.</summary>
    public string? EntraClientId { get; init; }

    /// <summary>True when an Entra ID client secret is stored. The secret itself is never returned.</summary>
    public bool HasStoredEntraClientSecret { get; init; }

    /// <summary>The requested Entra ID token scope (e.g. <c>api://&lt;backend-app-id&gt;/.default</c>).</summary>
    public string? EntraTokenScope { get; init; }

    /// <summary>
    ///     The interactive sign-in method used when no client secret is stored, as the
    ///     <see cref="XE_Local_AI_Engine.Client.Services.CloudProviders.EntraSignInMethod" /> enum name.
    /// </summary>
    public string EntraSignInMethod { get; init; } =
        nameof(Services.CloudProviders.EntraSignInMethod.ClientSecret);

    /// <summary>
    ///     The loopback redirect URI for <c>AuthorizationCode</c> sign-in. Null when not configured (the coordinator
    ///     falls back to the default). Not secret — round-trips for inline editing.
    /// </summary>
    public string? EntraAuthCodeRedirectUri { get; init; }
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

    /// <summary>The requested auth mode (ApiKey | ManagedIdentity | EntraId). Case-insensitive enum name.</summary>
    public string AuthMode { get; init; } = nameof(AzureFoundryAuthMode.ApiKey);

    /// <summary>Required only when <see cref="AuthMode" /> is <c>ApiKey</c>; ignored for managed identity.</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    ///     The requested wire surface (AzureDeployments | OpenAiV1). Case-insensitive enum name; unrecognized values
    ///     fall back to <c>AzureDeployments</c> (see <c>CloudSettingsEndpointDtoMapper.ParseApiSurface</c>).
    /// </summary>
    public string ApiSurface { get; init; } = nameof(AzureFoundryApiSurface.AzureDeployments);

    public IReadOnlyList<AzureFoundryModelDto> Models { get; init; } = [];

    /// <summary>
    ///     Custom request headers to persist. A secret header sent with a blank value keeps the previously stored value
    ///     (merge happens in the endpoint, Locked #10/#12).
    /// </summary>
    public IReadOnlyList<SaveAzureFoundryHeaderRequest> Headers { get; init; } = [];

    /// <summary>Operator-added extra allowed host suffixes (Locked #14). Shape-validated on save.</summary>
    public IReadOnlyList<string> AdditionalAllowedHostSuffixes { get; init; } = [];

    /// <summary>Required only when <see cref="AuthMode" /> is <c>EntraId</c>; ignored otherwise.</summary>
    public string? EntraTenantId { get; init; }

    /// <summary>Required only when <see cref="AuthMode" /> is <c>EntraId</c>; ignored otherwise.</summary>
    public string? EntraClientId { get; init; }

    /// <summary>
    ///     Write-only Entra ID client secret. A blank value on an existing EntraId connection keeps the previously
    ///     stored secret (merge happens in the endpoint, mirroring the custom-header secret merge, Locked #10/#12
    ///     pattern); a blank value with no stored secret selects interactive user sign-in instead of app-only
    ///     client-credentials.
    /// </summary>
    public string? EntraClientSecret { get; init; }

    /// <summary>Required only when <see cref="AuthMode" /> is <c>EntraId</c>; ignored otherwise.</summary>
    public string? EntraTokenScope { get; init; }

    /// <summary>
    ///     The interactive sign-in method when no client secret is configured (case-insensitive
    ///     <see cref="XE_Local_AI_Engine.Client.Services.CloudProviders.EntraSignInMethod" /> enum name). Ignored — and
    ///     coerced to <c>ClientSecret</c> — when a client secret is present.
    /// </summary>
    public string EntraSignInMethod { get; init; } =
        nameof(Services.CloudProviders.EntraSignInMethod.DeviceCode);

    /// <summary>
    ///     The loopback redirect URI for <c>AuthorizationCode</c> sign-in (e.g. <c>http://localhost:53682/signin-oidc</c>).
    ///     Blank selects the default. Ignored for every other sign-in method.
    /// </summary>
    public string? EntraAuthCodeRedirectUri { get; init; }
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
                ApiSurface = connection.ApiSurface.ToString(),
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
                AdditionalAllowedHostSuffixes = [.. connection.AdditionalAllowedHostSuffixes],
                EntraTenantId = NullIfBlank(connection.EntraTenantId),
                EntraClientId = NullIfBlank(connection.EntraClientId),
                // Never emit the stored secret — presence only.
                HasStoredEntraClientSecret = !string.IsNullOrWhiteSpace(connection.EntraClientSecret),
                EntraTokenScope = NullIfBlank(connection.EntraTokenScope),
                EntraSignInMethod = connection.EntraSignInMethod.ToString(),
                EntraAuthCodeRedirectUri = NullIfBlank(connection.EntraAuthCodeRedirectUri)
            }
        };
    }

    /// <summary>
    ///     Maps a save request to the stored config. Pure: the secret merges that need prior state run in the
    ///     endpoint and are passed in via <paramref name="mergedHeaders" /> and <paramref name="mergedEntraClientSecret" />
    ///     (Locked #12 pattern).
    /// </summary>
    public static StoredCloudProviderConfig ToStoredConfig(this SaveCloudSettingsRequest request,
        IReadOnlyList<StoredAzureFoundryHeader> mergedHeaders,
        string? mergedEntraClientSecret)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mergedHeaders);

        var authMode = ParseAuthMode(request.AuthMode);
        var isEntraId = authMode == AzureFoundryAuthMode.EntraId;
        var entraClientSecret = isEntraId ? mergedEntraClientSecret : null;

        return new StoredCloudProviderConfig
        {
            SchemaVersion = 2,
            ProviderName = request.ProviderName.Trim(),
            AzureFoundry = new StoredAzureFoundryConnection
            {
                Endpoint = request.Endpoint?.Trim() ?? string.Empty,
                AuthMode = authMode,
                ApiSurface = ParseApiSurface(request.ApiSurface),
                // Managed identity / Entra ID carry no key; drop anything supplied so it is never persisted.
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
                ],
                EntraTenantId = isEntraId ? NullIfBlank(request.EntraTenantId) : null,
                EntraClientId = isEntraId ? NullIfBlank(request.EntraClientId) : null,
                EntraClientSecret = entraClientSecret,
                EntraTokenScope = isEntraId ? NullIfBlank(request.EntraTokenScope) : null,
                EntraSignInMethod = ParseEntraSignInMethod(request.EntraSignInMethod, hasSecret: !string.IsNullOrWhiteSpace(entraClientSecret)),
                EntraAuthCodeRedirectUri = isEntraId ? NullIfBlank(request.EntraAuthCodeRedirectUri) : null
            }
        };
    }

    private static AzureFoundryAuthMode ParseAuthMode(string? authMode)
    {
        return Enum.TryParse<AzureFoundryAuthMode>(authMode?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : AzureFoundryAuthMode.ApiKey;
    }

    private static AzureFoundryApiSurface ParseApiSurface(string? apiSurface)
    {
        return Enum.TryParse<AzureFoundryApiSurface>(apiSurface?.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : AzureFoundryApiSurface.AzureDeployments;
    }

    // A configured client secret selects app-only client-credentials by default (Locked build contract §8: "derive
    // default: secret present -> ClientSecret"), carved out for AuthorizationCode — the one sign-in method that
    // legitimately wants both a secret (to authenticate code redemption) and a delegated scope. Any other requested
    // value with a secret present still coerces to ClientSecret, regardless of what the UI last had selected.
    private static EntraSignInMethod ParseEntraSignInMethod(string? signInMethod, bool hasSecret)
    {
        var parsedOk = Enum.TryParse<EntraSignInMethod>(signInMethod?.Trim(), ignoreCase: true, out var parsed);

        if (hasSecret)
        {
            return parsedOk && parsed == EntraSignInMethod.AuthorizationCode
                ? EntraSignInMethod.AuthorizationCode
                : EntraSignInMethod.ClientSecret;
        }

        return parsedOk && parsed is not (EntraSignInMethod.ClientSecret or EntraSignInMethod.AuthorizationCode)
            ? parsed
            : EntraSignInMethod.DeviceCode;
    }

    private static string? NormalizeApiKey(string? apiKey)
    {
        return string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
