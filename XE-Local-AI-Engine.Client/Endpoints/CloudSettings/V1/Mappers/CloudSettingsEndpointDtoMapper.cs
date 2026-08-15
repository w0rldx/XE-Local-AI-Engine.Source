namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.CloudProviders;

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
    ///     Maps the request's header rows onto the stored header shape so <see cref="CloudSettingsPolicy" /> (which
    ///     lives in the service layer and cannot see the request DTO) can validate them. A null name becomes empty —
    ///     the policy treats it as the blank-name row, exactly as the request shape did.
    /// </summary>
    public static IReadOnlyList<StoredAzureFoundryHeader> ToPolicyHeaders(this IReadOnlyList<SaveAzureFoundryHeaderRequest> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return
        [
            .. headers.Select(static header => new StoredAzureFoundryHeader
            {
                Name = header.Name ?? string.Empty,
                Value = header.Value,
                IsSecret = header.IsSecret
            })
        ];
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

    // A configured client secret selects app-only client-credentials by default (frozen build contract: secret
    // present -> ClientSecret), carved out for AuthorizationCode — the one sign-in method that
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
