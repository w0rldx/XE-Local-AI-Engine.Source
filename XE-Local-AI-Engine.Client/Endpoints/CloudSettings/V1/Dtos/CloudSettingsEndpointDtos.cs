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

    /// <summary>Operator-added extra allowed host suffixes. Not secret — round-trips for inline editing.</summary>
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
    ///     through the endpoint's secret-preserving merge.
    /// </summary>
    public IReadOnlyList<SaveAzureFoundryHeaderRequest> Headers { get; init; } = [];

    /// <summary>Operator-added extra allowed host suffixes. Shape-validated on save.</summary>
    public IReadOnlyList<string> AdditionalAllowedHostSuffixes { get; init; } = [];

    /// <summary>Required only when <see cref="AuthMode" /> is <c>EntraId</c>; ignored otherwise.</summary>
    public string? EntraTenantId { get; init; }

    /// <summary>Required only when <see cref="AuthMode" /> is <c>EntraId</c>; ignored otherwise.</summary>
    public string? EntraClientId { get; init; }

    /// <summary>
    ///     Write-only Entra ID client secret. A blank value on an existing EntraId connection keeps the previously
    ///     stored secret through the endpoint's custom-header-style secret merge; a blank value with no stored secret
    ///     selects interactive user sign-in instead of app-only
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
