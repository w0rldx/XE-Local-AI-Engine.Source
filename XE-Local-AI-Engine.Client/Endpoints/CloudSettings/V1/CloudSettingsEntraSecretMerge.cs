namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Secret-preserving merge of the incoming Entra ID client secret against the previously stored connection,
///     mirroring <see cref="CloudSettingsHeaderMerge" />. A blank incoming secret keeps the
///     previously stored one ONLY when the existing connection was already EntraId and had a secret stored — a
///     fresh connection, an auth-mode switch, or a blank secret with nothing stored NEVER inherits, so a secret is
///     never resurrected across a mode change.
/// </summary>
internal static class CloudSettingsEntraSecretMerge
{
    public static string? Merge(StoredAzureFoundryConnection? existing, string? incomingSecret)
    {
        var trimmed = string.IsNullOrWhiteSpace(incomingSecret) ? null : incomingSecret.Trim();
        if (trimmed is not null)
        {
            return trimmed;
        }

        return existing is { AuthMode: AzureFoundryAuthMode.EntraId } ? existing.EntraClientSecret : null;
    }
}
