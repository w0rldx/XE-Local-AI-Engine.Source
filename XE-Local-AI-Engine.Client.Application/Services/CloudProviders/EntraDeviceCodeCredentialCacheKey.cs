namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Builds the stable <see cref="IEntraLiveCredentialCache" /> key from a device-code sign-in identity (tenant,
///     client, scope — device-code carries no secret). Shared by the sign-in coordinator (writer, on success) and
///     the chat-client factory (reader, on every send) so a completed sign-in is found by the very next send.
/// </summary>
internal static class EntraDeviceCodeCredentialCacheKey
{
    public static string Create(string? tenantId, string? clientId, string? tokenScope)
    {
        return $"{tenantId}|{clientId}|{tokenScope}";
    }
}
